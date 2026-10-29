# Build your first plugin

Plugins add trusted in-process .NET contributions to Oscova. The host discovers plugin manifests for administration, then loads only valid plugins explicitly enabled by an administrator at startup.

> **Note:**
> In-process means the plugin code runs inside the Oscova application process. It is not sandboxed, so install only plugins you trust.



## Prerequisites

You need the .NET 10 SDK and a reproducible `Oscova.Agents` package reference and a declared supported framework range. You also need administrator access to enable the installed plugin.

## Create the project

Create a class library that targets .NET 10. The following project shape supports local development and keeps Oscova's framework assembly out of the plugin package:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <IsPackable>true</IsPackable>
    <PackageId>Example.Hello</PackageId>
    <Version>1.0.0</Version>
    <PackageType>OscovaPlugin</PackageType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Oscova.Agents" Version="4.0.0"
                      ExcludeAssets="runtime" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <None Include="oscova-plugin.json" Pack="true" PackagePath="\">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
    </None>
  </ItemGroup>
</Project>
```

Replace the illustrative compile version with the framework package you build against. A plugin compiled against an incompatible framework contract may fail to load.

## Implement the entry point

Every plugin entry type implements `IAgentPlugin`. The entry point must have a public parameterless constructor because the loader creates it with `Activator.CreateInstance`.

```csharp
using System.ComponentModel;
using Oscova.Agents;
using Oscova.Agents.Plugins;

namespace Example.Hello;

public sealed class MyAgentPlugin : IAgentPlugin
{
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddToolset(context => new MyToolset(() => context.UserTimeZone));
    }
}

public sealed class MyToolset(Func<TimeZoneInfo> userTimeZoneResolver) : IAgentToolset
{
    [DisplayName("say_hello")]
    [AgentTool]
    [Description("Returns a friendly greeting with the user's local time.")]
    public string SayHello(string name)
    {
        var localTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, userTimeZoneResolver());
        return $"Hello, {name}. Your local time is {localTime:t}.";
    }
}
```

The `AddToolset` factory is registered during startup and invoked later for each user scope. Use its `AgentPluginContext` for credentials, guarded HTTP clients, logging, and the user's time zone. Only methods marked `[AgentTool]` are exposed. Do not reach into the host dependency-injection container.

Keep `Configure` and the toolset factory cheap and synchronous. Neither API accepts a cancellation token. Do not block on asynchronous work, call remote services, read credentials, or start background work while the plugin is loading. Tool methods can perform cancellable asynchronous work when the tool contract requires it.

`AgentPluginBuilder` also supports `AddModelProvider`, `AddSpeechRecognitionProvider`, and `AddSpeechSynthesisProvider`. Start with one toolset. Add provider contributions only when the plugin implements the corresponding public provider interface.

## Add the manifest

Add `oscova-plugin.json` to the project root. The project file above copies it beside the built assembly and places it at the package root.

```json
{
  "id": "example.hello",
  "name": "Example Hello Tools",
  "version": "1.0.0",
  "frameworkVersionRange": "[4.0.0,5.0.0)",
  "assembly": "Example.Hello.dll",
  "entryType": "Example.Hello.MyAgentPlugin"
}
```

The assembly path is relative to the manifest directory and must stay inside that directory. `entryType` is the fully qualified name of the public class that implements `IAgentPlugin`.

Follow [Plugin Manifest](references/developer-guide-plugins-manifest.md) to publish the same supported range as the package's `Oscova.Agents` dependency. The exact compile reference above is separate from that supported range. The illustrative major-4 range applies only if you support all those releases. Development releases can break APIs in minor versions, so narrow your range to the releases you have verified.

## Build and install locally

Build the plugin:

```console
dotnet build -c Release
```

For a CLI-managed installation, Oscova discovers plugins from `.oscova/app_data/plugins` under the user profile. A Host started directly from a development or published application uses its package-relative `app_data/plugins` by default. If `OSCOVA_APP_DATA_DIR` is set, both use the `plugins` directory beneath that explicit root instead.

```text
<effective-app-data>/
  plugins/
    example.hello/
      oscova-plugin.json
      Example.Hello.dll
      other-plugin-dependencies.dll
```

Copy the manifest, plugin assembly, dependency metadata, and plugin-private dependencies from the build output. Keep dependency files at the paths described by the generated dependency metadata.

After copying files, open **Settings** > **Agents & AI** > **Plugins** as an administrator. Review the manifest details, turn on **Load On Next Restart**, save, and restart the host.

Plugin files are discovered before they are enabled. A newly installed third-party plugin remains inactive until an administrator saves the activation choice and restarts. Official components provisioned by Oscova during startup are enabled automatically after a successful first installation.

## Understand the activation boundary

On startup, Oscova validates discovered manifests, filters them against the saved activation list, loads each enabled assembly, creates its `IAgentPlugin` entry point, and calls `Configure`. Successful contributions remain loaded for the life of that host process.

Installing files, enabling or disabling a plugin, or replacing its assembly does not change the running process. Save the activation choice and restart the host. Oscova does not hot-unload or hot-reload plugins. Online updates to an installed plugin are staged for the next startup and preserve the saved activation choice.

## Package for online install

If you publish through the online plugin browser, package the plugin as a NuGet package with package type `OscovaPlugin`. The installer expects `oscova-plugin.json` at the package root and the declared assembly as a selected `net10.0` runtime asset. Ordinary `PackageReference` dependencies such as `Humanizer.Core` stay in the NuGet dependency graph.

The installer selects the latest compatible stable version from NuGet.org, restores managed, native, and resource dependencies for the current supported RID, and writes the loadable component into one plugin directory. It also copies ordinary dependency files under `content/**` into that dependency's package directory. Root-package content, `contentFiles/**`, analyzers, build assets, tools, and scripts are ignored. Package build targets are never executed, and dependencies on another Oscova plugin are unsupported. The requested plugin package must not exceed 25 MB.

For this example, add `Example.Hello.nuspec` beside the project. The private compile reference does not export a framework dependency, so this package manifest declares the supported range:

```xml
<?xml version="1.0"?>
<package>
  <metadata>
    <id>Example.Hello</id>
    <version>1.0.0</version>
    <authors>Example developer</authors>
    <description>Example greeting tools for Oscova.</description>
    <packageTypes><packageType name="OscovaPlugin" /></packageTypes>
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="Oscova.Agents" version="$frameworkVersionRange$" />
      </group>
    </dependencies>
  </metadata>
  <files>
    <file src="oscova-plugin.json" target="" />
    <file src="bin/Release/net10.0/Example.Hello.dll" target="lib/net10.0" />
  </files>
</package>
```

Build and pack from PowerShell. Read the range from the JSON manifest so both declarations use one value. Escaping its comma prevents MSBuild from splitting the property argument:

```powershell
dotnet build -c Release
$range = (Get-Content oscova-plugin.json -Raw | ConvertFrom-Json).frameworkVersionRange
$escapedRange = $range.Replace(',', '%2C')
dotnet pack -c Release --no-build -p:NuspecFile=Example.Hello.nuspec "-p:NuspecProperties=frameworkVersionRange=$escapedRange"
```

If your plugin adds ordinary package dependencies, include them in the same dependency group. Inspect the built package before publishing and confirm its framework dependency matches `frameworkVersionRange`.

See [Plugin Settings](references/user-guide-settings-and-admin-plugins.md) for the complete package shape and administration workflow.

## Validate the plugin

Before distributing the plugin, verify:

- the manifest is present and its `assembly` and `entryType` values match the build output.
- the entry type is public, implements `IAgentPlugin`, and has a public parameterless constructor.
- a clean test plugin root contains all plugin-private dependencies.
- the plugin appears as valid in administration before you enable it.
- the plugin becomes active only after saving **Load On Next Restart** and restarting.
- reflected tool names are the qualified names you intend to make available to agents.

## Troubleshooting

**The plugin is installed but inactive**

Installation only makes the plugin discoverable. Enable **Load On Next Restart**, save, and restart the host.

**The manifest is invalid**

Check that every required value is present, the assembly path is relative and contained within the plugin folder, and the named assembly exists. Duplicate plugin IDs are compared without regard to case. Only one can be loaded.

**The plugin fails to load**

Check the administration diagnostic. Confirm that `entryType` names a public `IAgentPlugin` implementation with a public parameterless constructor, all required plugin-private dependencies were copied, and `frameworkVersionRange` includes the Host framework version. An exception from the constructor or `Configure` skips that plugin while other valid plugins continue loading.

**The plugin is active but its toolset is missing**

Check the runtime logs for a toolset-construction failure, then verify credentials and other user-scoped inputs used by the factory. A failing factory skips that contribution for the affected user without unloading the plugin. Active tools must also be made available through the agent's tool configuration.

**A rebuilt plugin still behaves like the old version**

Replace the complete plugin output, stop the running host, and restart it. Plugin load contexts remain active for the process lifetime.

## Settings and execution contributions

For a compiled settings UI, create a .NET 10 Razor class library, retain the compile-only framework package reference, and add `Microsoft.AspNetCore.App`. Reuse the Echo channel component and [Compiled Plugin Settings](references/developer-guide-plugins-compiled-settings.md) authoring contract. The standalone preview uses unchanged installed Host styles.

## Related articles

- [Echo Plugin](references/developer-guide-examples-echo-plugin.md)
- [Manifest](references/developer-guide-plugins-manifest.md)
- [Plugin Builder](references/developer-guide-plugins-plugin-builder.md)
- [IAgentPlugin](references/developer-guide-plugins-iagent-plugin.md)
