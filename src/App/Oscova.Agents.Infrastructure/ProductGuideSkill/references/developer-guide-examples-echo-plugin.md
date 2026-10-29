# Echo plugin

The Echo plugin is a complete plugin test fixture. Use it to see how a manifest, an `IAgentPlugin` entry point, reflected toolsets, providers, a task, and a compiled settings page fit together.

> **Caution:**
> Plugins run as trusted code inside the Oscova process. The Echo plugin is a test fixture, not a security boundary or a production integration.

## Find the sample

The sample lives in `tests/Oscova.Agents.Plugins.Echo/` in the public `Oscova.Agents` repository:

| File | Purpose |
| --- | --- |
| `Oscova.Agents.Plugins.Echo.csproj` | Targets .NET 10, references `Oscova.Agents`, and declares an `OscovaPlugin` NuGet package. |
| `oscova-plugin.json` | Identifies the plugin assembly and entry type. |
| `EchoAgentPlugin.cs` | Contains the entry point, toolset, model-provider double, and speech-synthesis-provider double. |
| `EchoChannelSettingsComponent.razor` | Implements the independent settings page and its Save note action. |
| `README.md` | Describes the package on NuGet. |

The project is a .NET 10 Razor class library. These project excerpts keep the host's framework assembly out of the package and place the manifest at the package root:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <EnableDynamicLoading>true</EnableDynamicLoading>
  <PackageId>Oscova.Agents.Plugins.Echo</PackageId>
  <Version>$(EchoPluginVersion)</Version>
  <PackageType>OscovaPlugin</PackageType>
</PropertyGroup>

<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
  <PackageReference Include="Humanizer.Core" Version="3.0.10" />
  <PackageReference Include="Oscova.Agents" Version="[$(OscovaAgentsVersion)]"
                    IncludeAssets="compile" PrivateAssets="all" />
</ItemGroup>

<ItemGroup>
  <None Include="oscova-plugin.json" Pack="true" PackagePath="\">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </None>
</ItemGroup>
```

The project uses `Microsoft.NET.Sdk.Razor`. Its shared build configuration supplies `EchoPluginVersion` and `OscovaAgentsVersion`, stamps the manifest tokens, and exports the framework compatibility range into NuGet dependency metadata. Outside that repository, supply your own version properties and packaging setup as described in [Build Your First Plugin](references/developer-guide-plugins-first-plugin.md). The result contains `oscova-plugin.json` at the package root, `Oscova.Agents.Plugins.Echo.dll` under `lib/net10.0/`, and an ordinary dependency on `Humanizer.Core`.

## Read the manifest

The checked-in manifest is:

```json
{
  "id": "oscova.agents.plugins.echo",
  "name": "Oscova Echo Plugin",
  "version": "$version$",
  "frameworkVersionRange": "[$frameworkVersion$]",
  "assembly": "Oscova.Agents.Plugins.Echo.dll",
  "entryType": "Oscova.Agents.Plugins.Echo.EchoAgentPlugin"
}
```

The checked-in tokens are replaced before copying and packing. The installed manifest contains concrete versions. The `assembly` path is relative to the manifest directory. The `entryType` value is the namespace-qualified name of the public class that implements `IAgentPlugin`.

## Register the contributions

The entry point uses the public `AgentPluginBuilder` methods and chains their returned builder. This reduced example shows its basic tool and provider registrations:

```csharp
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Plugins.Echo;

public sealed class EchoAgentPlugin : IAgentPlugin
{
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddToolset(_ => new EchoToolset())
            .AddModelProvider(_ => new EchoModelProvider())
            .AddSpeechSynthesisProvider(_ => new EchoSpeechSynthesisProvider());
    }
}
```

`Configure` records registrations. The complete fixture also registers an encrypted-marker task, a node-capable project toolset, a channel metadata probe with its credential, localization, and an independent settings page. Toolset and task factories run later with an `AgentPluginContext` for a user scope. Provider factories run during startup with one shared context for this plugin. Echo's task uses `UserDataDirectory` and `Services.Encryption` to save the task name as `task-{task.UserId}-{task.Id}.txt.enc`. `EchoToolset.Pluralize` separately proves that a plugin-private package dependency loads. See [Plugin Context](references/developer-guide-plugins-context.md) for encryption and file ownership.

The sample model provider and speech synthesis provider are catalog test doubles:

- `EchoModelProvider` reports type `echo`, endpoint `https://echo.invalid/`, model `echo-1`, and no embedding support. Creating a client throws `NotSupportedException`.
- `EchoSpeechSynthesisProvider` reports type `echo-tts`, reports one voice named `echo`, and returns an empty 24 kHz, mono, 16-bit payload.

Do not use either provider as an implementation template for a real external service. They exist to prove registration and catalog integration without a network call.

## Reflect the toolset

The toolset implements `IAgentToolset`. `DisplayName` supplies the tool's base name, while `Description` supplies the model-visible description:

```csharp
using System.ComponentModel;
using Humanizer;
using Oscova.Agents;
using Oscova.Agents.Credentials;

namespace Oscova.Agents.Plugins.Echo;

[CredentialSlot(
    Key = "test-echo",
    Label = "Test Echo",
    Description = "Credential used by the plugin loader test assembly.")]
public sealed class EchoToolset : IAgentToolset
{
    [DisplayName("test_echo")]
    [AgentTool]
    [Description("Returns the supplied text unchanged.")]
    public static string Echo(string text)
    {
        return text;
    }

    [DisplayName("test_pluralize")]
    [AgentTool]
    [Description("Returns the plural form of an English word.")]
    public static string Pluralize(string text)
    {
        return text.Pluralize();
    }
}
```

Neither method resolves or uses the declared `test-echo` credential. The attribute is fixture metadata only. `test_pluralize` calls `Humanizer.Core`, which is restored into the installed plugin's package-scoped layout.

Oscova builds the tool's qualified wire name from three values:

| Source | Normalized segment |
| --- | --- |
| Plugin ID `oscova.agents.plugins.echo` | `oscova_agents_plugins_echo` |
| Toolset class `EchoToolset` | `echo` |
| Display name `test_echo` | `test_echo` |

The resulting name is:

```text
oscova_agents_plugins_echo__echo__test_echo
```

Use `ToolNaming.BuildToolsetQualifiedName("oscova.agents.plugins.echo", "echo", "test_echo")` in code instead of assembling this string yourself.

The pluralization tool follows the same rule and ends in `__test_pluralize`.

## Know what the focused tests prove

`AgentPluginLoaderTests.LoadDirectory_LoadsTestPluginAndReflectsToolset` copies the compiled fixture into a temporary plugin root and proves that:

- the manifest values above survive loading.
- `EchoToolset` is captured as an `IAgentToolset` contribution.
- `EchoModelProvider` and `EchoSpeechSynthesisProvider` are captured, while no speech recognition provider is registered.
- the toolset factory creates an `IAgentToolset`.
- reflection publishes `test_echo` under the qualified name above.

The loader also reports diagnostics for a missing entry type, a type that does not implement `IAgentPlugin`, and an entry type without a public parameterless constructor.

## Troubleshooting

**The manifest loads but the plugin does not**

Check that the built DLL is beside the manifest, the `version` in the project and manifest agree, and `entryType` still names `Oscova.Agents.Plugins.Echo.EchoAgentPlugin`.

**The tool cannot be found**

Use the complete qualified name, not `test_echo` by itself. Confirm the plugin was enabled before startup and the agent allows `oscova_agents_plugins_echo__echo__test_echo`.

**The Echo model provider throws**

That is expected. It is a catalog double and deliberately cannot create a chat or embedding client.

**A package install rejects an adapted sample**

Keep `oscova-plugin.json` at the package root, the declared assembly under `lib/net10.0/`, and the package type set to `OscovaPlugin`. See the manifest article for the complete installer contract.

## Related articles

- [Build Your First Plugin](references/developer-guide-plugins-first-plugin.md)
- [Plugin Manifest](references/developer-guide-plugins-manifest.md)
- [IAgentPlugin](references/developer-guide-plugins-iagent-plugin.md)
- [Testing Plugins](references/developer-guide-examples-testing-plugins.md)

## Compiled settings example

Echo includes an independent compiled settings page in Preferences. Its Save note button writes a plugin-owned plaintext text file through the supplied user settings path. It deliberately does not use the optional bound settings store. The packaged component, satellite resources and shared Blazor type identities are part of plugin-loader acceptance. See [Compiled Plugin Settings](references/developer-guide-plugins-compiled-settings.md). Preview appearances alone do not prove admission, authorization or execution.
