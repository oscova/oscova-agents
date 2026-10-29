# IAgentPlugin

`IAgentPlugin` is the entry point Oscova instantiates after a plugin manifest and assembly pass validation. Its job is deliberately small: register contributions with `AgentPluginBuilder`.

> **Note:**
> An entry point is the public class named by `entryType` in `oscova-plugin.json`.

## Contract

```csharp
using Oscova.Agents.Plugins;

namespace Example.Hello;

public sealed class MyAgentPlugin : IAgentPlugin
{
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddToolset(context => new MyToolset(() => context.UserTimeZone));
    }
}
```

Here `MyToolset` accepts a `Func<TimeZoneInfo>` so it can read the current preference for each call. See [Build Your First Plugin](references/developer-guide-plugins-first-plugin.md) for its implementation.

`IAgentPlugin` declares one method:

```csharp
void Configure(AgentPluginBuilder builder);
```

The entry class must be public and have a public parameterless constructor. For each successful load, the loader creates a new entry instance and a new `AgentPluginBuilder`, calls `Configure` once, and snapshots the declared contributions only if the call succeeds.

## Lifecycle

1. Oscova inspects manifests without loading plugin assemblies.
2. At startup, the saved activation list selects which valid plugins may load.
3. The loader resolves the assembly and `entryType`, verifies `IAgentPlugin`, and creates the entry instance.
4. The loader calls `Configure` and snapshots valid builder contributions, reporting rejected declarations individually.
5. Provider factories run once during startup with one shared `AgentPluginContext` per plugin. Toolset factories run later with one separate context per plugin and user scope.
6. Loaded plugins and their assembly load contexts remain active for the host process lifetime.

Enabling, disabling, or replacing a plugin takes effect only after the administrator saves the next-startup selection and restarts the host. There is no hot unload or reload.

## Configure registrations only

Use `Configure` to call the public registration methods on `AgentPluginBuilder`:

- `AddToolset<TToolset>(Func<AgentPluginContext, TToolset>)` registers a non-null factory for a class that implements `IAgentToolset`.

- `AddModelProvider<TProvider>(Func<AgentPluginContext, TProvider>)` registers a non-null factory for a class that implements `IModelProvider`.

- `AddSpeechRecognitionProvider<TProvider>(Func<AgentPluginContext, TProvider>)` registers a non-null factory for a class that implements `ISpeechRecognitionProvider`.

- `AddSpeechSynthesisProvider<TProvider>(Func<AgentPluginContext, TProvider>)` registers a non-null factory for a class that implements `ISpeechSynthesisProvider`.

- `AddChannel(IAgentChannel)` registers channel metadata, validation, and an `IChannelConnection` factory.

- `AddCredential` registers a structured credential slot.

- `AddTask` registers task defaults, with optional deterministic or agent preparation handler factories.

- `AddAgentTemplate` supplies starter-agent definitions.

- `AddInstruction` supplies a named default instruction.

- `AddOAuthProvider` declares a connected-account provider and its app credential.

- `AddSettingsPanel` registers an independent settings page or an owned inline component.

- `AddExecutionProvider` and `AddContainerLauncher` register device execution and container-launch capabilities.

- `UseResources<TResource>()` registers the plugin's display-string marker.

Each method returns the same builder so registrations can be chained. Invalid individual declarations produce diagnostics while later declarations still register. See [Plugin Builder](references/developer-guide-plugins-plugin-builder.md) for channel lifecycle and settings ownership.

`Configure` is synchronous and has no cancellation token. Contribution factories are also synchronous. Keep both paths deterministic and inexpensive:

- do not call remote APIs.
- do not resolve user credentials.
- do not block on asynchronous work.
- do not start background work.
- do not retain the builder as mutable runtime state.

Perform setup that depends on a user when the toolset factory receives `AgentPluginContext`. Provider factories may use `CreateHttpClient`, `LoggerFactory`, and the Host-tracked download operation, but their resolver does not expose user credentials. Perform cancellable asynchronous work inside the later tool or provider operation when its contract supports cancellation. Provider operations receive selected API keys through their existing parameters.

> **Caution:**
> Plugins are trusted in-process assemblies. `IAgentPlugin` is not a sandbox boundary, so install only assemblies you trust to run inside the host process.

## Failure behavior

Plugin discovery and loading are failure-isolated:

- an invalid manifest is reported without loading its assembly.
- a missing entry type, incompatible entry type, constructor failure, or `Configure` exception records an error diagnostic and skips the plugin.
- one failed plugin does not prevent other valid enabled plugins from loading.
- a toolset-factory exception skips that contribution for the affected user while the plugin and its other contributions remain available.
- a provider-activation failure omits that provider while other providers continue loading.

Normal user-configuration problems, such as a missing API key, should not make `Configure` fail. Resolve credentials for the user scope and return actionable guidance from the operation that needs them.

Loader diagnostics can include exception messages and can be shown to administrators. Do not put credentials, tokens, or other secrets in exceptions thrown by the entry constructor or `Configure`.

## Troubleshooting

**The entry type is rejected**

Verify that the manifest uses the fully qualified type name, the type is public, it implements the same compatible `Oscova.Agents.Plugins.IAgentPlugin` contract as the host, and it has a public parameterless constructor.

**Configure fails**

Move remote calls, credential resolution, and user-dependent setup out of `Configure`. The loader reports the exception as a plugin load diagnostic and does not publish partial registrations from that plugin.

**A toolset is absent for one user**

Check runtime logs for the toolset factory failure. Validate only the inputs used to construct that user's contribution. Do not move the failing setup back into `Configure`, because that would prevent the whole plugin from loading.

**A change is not visible**

Restart the host after changing activation or plugin files. A successful load is fixed for that process lifetime.

## Contribute agent templates

Every template plugin uses the same contract:

```csharp
builder.AddAgentTemplate(new AgentTemplateDefinition
{
    Id = "release-notes",
    Name = "Release Notes",
    Description = "Drafts release notes from supplied changes.",
    SystemInstruction = "Draft clear release notes from the changes supplied by the user.",
    SetupInstructions = "Review selected tools and allowed directories in the editor.",
    RequestedToolNames =
    [
        ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.ReadFile))
    ]
});
```

Use `Oscova.Agents.Toolsets` for `CodingToolset`. `Id` is unique within the plugin and accepts lowercase ASCII letters, digits and hyphens. Name, description and instructions must contain text. Setup guidance is optional plain text.

For referenced types, use the typed naming overload and `nameof`. For optional providers or another host's tools, use `ToolNaming.BuildToolsetQualifiedName(origin, owner, toolName)` with that provider's canonical identity. This avoids loading optional assemblies. Those names are integration contracts. Check them against actual registrations when provider versions change.

Oscova copies `RequestedToolNames` into the new agent and applies the same availability and execution rules for every plugin. Declarations do not grant credentials, install providers, change approval policy or enable network access. Users can edit the selected tools afterward.

Optional `ProfilePictureKey` names an existing Host seed picture, never a path or URL. Unrecognized keys use the normal picture. The Host uses the same rules for every plugin.

Saved copies survive plugin updates, disable and removal. Activation changes take effect after restart.

## Settings and execution contributions

A plugin may also register compiled settings panels, execution providers and the narrow container process launch role through its existing builder. Keep factories free of Docker probes or builds. See [Compiled Plugin Settings](references/developer-guide-plugins-compiled-settings.md).

## Related articles

- [Build Your First Plugin](references/developer-guide-plugins-first-plugin.md)
- [Plugin Manifest](references/developer-guide-plugins-manifest.md)
- [Plugin Builder](references/developer-guide-plugins-plugin-builder.md)
- [Plugin Context](references/developer-guide-plugins-context.md)
