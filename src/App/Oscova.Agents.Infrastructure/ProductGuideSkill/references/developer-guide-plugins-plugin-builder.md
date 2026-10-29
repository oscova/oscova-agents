# Plugin builder

Use `AgentPluginBuilder` inside `IAgentPlugin.Configure` to declare the toolset and provider factories an enabled plugin contributes to Oscova.

> **Note:**
> The builder records registrations. It does not create toolsets, start providers, resolve credentials, or call remote services while `Configure` runs.



## Supported registrations

`AgentPluginBuilder` exposes registration methods for runtime and display contributions:

| Method | Type requirement | What the builder stores |
| --- | --- | --- |
| `AddToolset<TToolset>(Func<AgentPluginContext, TToolset>, bool supportsNodeExecution = false)` | `TToolset` is a class implementing `IAgentToolset` | The toolset type, factory and optional node support |
| `AddModelProvider<TProvider>(Func<AgentPluginContext, TProvider>)` | `TProvider` is a class implementing `IModelProvider` | The provider type and factory |
| `AddSpeechRecognitionProvider<TProvider>(Func<AgentPluginContext, TProvider>)` | `TProvider` is a class implementing `ISpeechRecognitionProvider` | The provider type and factory |
| `AddSpeechSynthesisProvider<TProvider>(Func<AgentPluginContext, TProvider>)` | `TProvider` is a class implementing `ISpeechSynthesisProvider` | The provider type and factory |
| `AddChannel(IAgentChannel)` | The channel implements `IAgentChannel` | Channel metadata, validation, and a connection factory |
| `AddOAuthProvider(definition)` | `OAuthProviderDefinition` | Account metadata, endpoints, scopes, identity reader, and its app credential |
| `AddCredential(declaration)` | `CredentialDeclaration` | A structured credential slot |
| `AddAgentTemplate(definition)` | `AgentTemplateDefinition` | Content, requested tools and an optional picture hint |
| `AddInstruction(key, label, defaultInstruction)` | A unique lowercase key, label and default Markdown | An instruction default data contribution |
| `AddTask(key, definitionFactory)` and its single-handler overloads | A unique safe task key and a definition factory | Task defaults with at most one execution handler factory |
| `UseResources<TResource>()` | A resource marker in the plugin assembly | The plugin's display-string family |
| `AddSettingsPanel(contribution)` | `AgentSettingsContribution` | A compiled component for an independent settings page or an owned inline slot |
| `AddExecutionProvider(contribution)` | `AgentExecutionContribution` | Device execution metadata and a runtime factory |
| `AddContainerLauncher(contribution)` | `AgentContainerLaunchContribution` | A factory for the containerized MCP process-launch role |

Each method returns the same builder, so registrations can be chained:

```csharp
using System.ComponentModel;
using Oscova.Agents;
using Oscova.Agents.Plugins;

public sealed class ExampleAgentPlugin : IAgentPlugin
{
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddToolset(_ => new ExampleToolset());
    }
}

public sealed class ExampleToolset : IAgentToolset
{
    [AgentTool]
    [DisplayName("example_echo")]
    [Description("Returns the supplied text unchanged.")]
    public static string Echo(string text)
    {
        return text;
    }
}
```

Chain `AddModelProvider`, `AddSpeechRecognitionProvider`, or `AddSpeechSynthesisProvider` after `AddToolset` when the plugin implements those provider interfaces. Channels, accounts, and credentials use the declaration methods above. The Google Workspace and Microsoft Graph plugins each register their account declaration and vendor toolsets through this builder. Settings pages use the compiled component contract. There is no supported builder registration for middleware, background services, HTTP routes, or arbitrary host services.

## Toolset lifecycle and naming

`AddToolset` requires a non-null factory, and that factory must return a non-null toolset. Oscova calls the factory once while assembling each user scope and passes an `AgentPluginContext` for that user. A toolset can serve multiple runs in that user scope, so keep run-specific state inside each method call.

Set `supportsNodeExecution: true` only for toolsets that can execute on a project device without server services or credentials. Use `context.GetAllowedDirectories()` during each invocation to obtain its authorized directories, and enforce them through the framework's file-access helpers. Do not cache invocation directories in the factory. Enabling the plugin then makes its compatible toolsets eligible for automatic deployment. This declaration does not sandbox native plugin code.

Return a new toolset when it depends on credentials, HTTP clients, logging, or the user's time zone. Do not cache the context, resolver, or toolset in static state. Treat clients returned by `CreateHttpClient` as host-managed and do not dispose them from the toolset.

Plugin tool methods use the same reflection contract as built-in toolsets:

- Only public methods marked `[AgentTool]` become tools. Helpers and accessors remain private to the toolset.
- `[DisplayName]` supplies the base tool name. Without it, the method name is used.
- `[Description]`, `[ToolPolicy]`, and `[ToolNetworkScope]` provide optional metadata.
- Default approval is `ToolApprovalMode.Default`.
- Default network scope is `NetworkScope.None`.

The qualified wire name begins with the plugin manifest ID, followed by the toolset owner and tool name. The owner uses a nonblank class-level `[DisplayName]` when present, otherwise the class name with a trailing `Toolset` removed. Every segment is normalized. For example, plugin `acme.search`, class `ExampleToolset` without a class display name, and method display name `search_records` produce `acme_search__example__search_records`. Derive the owner with `ToolNaming.GetToolsetOwner` when constructing names in code.

## Provider lifecycle

The three provider methods register factories. Oscova invokes them during startup and shares the resulting providers across users. All provider factories from one plugin receive the same `AgentPluginContext`. Factories from different plugins receive different contexts. Use `CreateHttpClient` and `LoggerFactory` for supported host capabilities. The provider context does not expose user credential values.

Keep contributed providers stateless and thread-safe. Runtime calls receive provider configuration, the selected plaintext API key, and cancellation through the corresponding public provider interface.

A model provider can implement `IModelDownloadProvider` when its SDK owns model acquisition. Oscova then offers **Download model** beside the typed Model ID, independently of catalog fetching. `DownloadModelAsync` receives a snapshot of `ModelProviderConfig`, optional `IProgress<ModelDownloadProgress>`, and cancellation. Use the snapshot's endpoint and model ID. Report `Percent` for the current transfer or leave it null for indeterminate progress, and optionally supply `Status` text. Complete the task only when acquisition succeeds and preserve useful provider exceptions. The existing dialog shows progress and returns errors to the calling screen. Its Cancel and header close actions cancel the operation token. Model storage remains owned by the provider.

Return `true` from `IModelProvider.DefaultIsLocalEndpoint` only when the provider is local by design. A saved endpoint-scope choice still takes precedence.

A successfully activated provider can implement `IAsyncDisposable` or `IDisposable`. Oscova disposes owned providers during host shutdown and releases rejected providers during activation. Plugins still cannot register background services or arbitrary shutdown callbacks.

Provider `Type` values are stable product identifiers. Use a unique value for each model, recognition, or synthesis provider. Built-in speech providers take precedence over plugin providers with the same `Type`. Among speech plugins, the first provider registered for a `Type` is retained and each later collision produces an operator warning naming the dropped implementation.

### Speech routing

`SpeechRecognitionRouter` and `SpeechSynthesisRouter` resolve the exact registered provider whose `Type` matches the supplied `SpeechProviderConfig`. A host selects the configuration and supplies the resolved API key, offline requirement, download URL, and session input needed by each operation. The routers reject non-local endpoints when offline operation is required, before calling the provider.

`SpeechProviderKeys` is a non-exhaustive set of well-known identifiers, not an allowlist. Choose a stable, unique type for your plugin provider and register it through the builder. It routes without adding a framework constant.

Both speech provider interfaces expose nullable `DefaultModel` metadata. Synthesis also exposes nullable `DefaultVoiceId`. Supply your runtime fallback values through these getters so hosts can seed configuration and show accurate defaults. Return `null` when no default exists. Existing implementations must implement these getters when rebuilding against this framework contract.

## Channel registration and lifecycle

A channel plugin implements `IAgentChannel` and declares its runtime title, optional display keys, embedded icon and help, required credential keys and notification support. Settings pages are separate compiled component contributions. See [Compiled plugin settings](references/developer-guide-plugins-compiled-settings.md) for page registration and lifecycle controls.

```csharp
builder.UseResources<PluginText>()
    .AddCredential(DiscordChannel.Credential)
    .AddChannel(new DiscordChannel());
```

Registration performs no network activity. `ReadConfigurationAsync(context, requireComplete, cancellationToken)` reads and validates your own saved model. Return an immutable `ChannelRuntimeConfiguration` with startup intent, optional agent ID and a connection factory capturing one consistent private configuration snapshot. The host consumes those operational facts without locating or parsing your files. Startup inspection permits incomplete disabled settings, but the reader must require completeness if its snapshot enables startup. Actual Start requires complete configuration.

The factory receives `ChannelConnectionContext`, containing an `IChannelEventSink` and a connection-owned HTTP transport. Your captured model remains private to the plugin. Implement `StartAsync`, `StopAsync`, `SendNotificationAsync` and `DisposeAsync` on `IChannelConnection`. Honor cancellation and release SDK resources and event subscriptions on disposal. Credentials declared with `AddCredential` join the credential allowlist. Official bot tokens stay in the declared credential store.

Send normalized `ChannelMessage` values, including sender identity and `IsBot`. Authorize the sender before calling the Host sink. Supply attachment metadata with lazy cancellable stream openers, never application filesystem paths. The sink returns after admission so the SDK can deliver later replies while an agent turn waits. The application owns the typing lease for the admitted turn and handles its agent, approval, and attachment policies.

Plugins own their display resources. Register a resource marker with `UseResources<TResource>()`, keep the marker in the plugin's root namespace, and place its `PluginText.resx` family under `Resources`. Set `<EmbeddedResourceUseDependentUponConvention>false</EmbeddedResourceUseDependentUponConvention>` when the marker source sits beside the resource files, so the embedded name includes `Resources`. The Host resolves strings for the current user and falls back to the plugin's neutral text. Channel help and SVG icons are declared embedded resources. The Host reads only active declarations, renders Markdown through its safe help renderer, and uses SVG silhouettes that inherit the current theme color.

Saving persists configuration first and restarts an existing connection. A stopped channel remains stopped. Failed persistence does not restart. A restart failure leaves the saved values intact. Plugin enablement and removal follow restart-based activation.

## Account registration

Call `AddOAuthProvider` with an `OAuthProviderDefinition` to publish an account provider and its app credential together. Do not separately register that credential. The declaration supplies display text, HTTPS endpoints, scopes, optional authorization parameters, and a pure `ReadConnectedEmail` JSON reader. It must perform no network or credential work during registration.

The Google Workspace plugin uses this registration alongside its three toolset factories. Oscova renders account cards and the operator credential editor from the declaration. The application handles consent, encrypted storage, refresh, and user isolation. Account declarations supply text and fields. Separate settings contributions can supply compiled UI components. See [Plugin Credentials](references/developer-guide-plugins-credentials.md) for the supported fields and validation contract.

## Ordering and duplicates

A new builder starts with empty collections. It appends valid registrations in call order and exposes them through read-only lists:

- `Toolsets`
- `ModelProviders`
- `SpeechRecognitionProviders`
- `SpeechSynthesisProviders`
- `Channels`
- `OAuthProviders`
- `Credentials`
- `AgentTemplates`
- `Tasks`
- `Instructions`
- `SettingsPanels`
- `ExecutionProviders`
- `ContainerLaunchers`

The builder requires unique task keys and channel IDs. Invalid individual declarations become diagnostics without discarding valid declarations before or after them. Register each contribution once. Repeated toolsets can produce duplicate qualified tool names, which Oscova rejects instead of replacing the earlier tool. The Host retains the first accepted provider type and disposes rejected provider instances.

## Failure behavior

- An invalid individual declaration becomes a load diagnostic while later registrations continue.
- A factory that returns null cannot produce a valid toolset and fails when Oscova publishes the tools.
- If `Configure` throws, the plugin is skipped and a load diagnostic is recorded.
- If a toolset factory throws for one user scope, that contribution is skipped for that user and the failure is logged.
- If a provider factory throws or returns null, that provider is skipped and the failure is logged.
- Thrown factory and provider-construction failures are isolated, so later contributions are still attempted.
- Invalid reflected metadata or duplicate qualified tool names are publication errors, not user-setup failures.

Keep expected setup failures, such as a missing credential or unavailable remote service, inside the tool or provider operation. Return actionable guidance for user-correctable failures and honor the supplied cancellation token.

The builder has no arbitrary background-service registration. A channel connection owns only its transport resources through `IAsyncDisposable`. Do not start resources during `Configure`.

## Settings and execution contributions

`AddSettingsPanel(AgentSettingsContribution)` registers an independent settings page or an owned execution, speech recognition, or speech synthesis panel. Channel plugins declare their own independent pages. `AddExecutionProvider(AgentExecutionContribution)` registers device execution metadata and a runtime factory. `AddContainerLauncher(AgentContainerLaunchContribution)` registers the narrow containerized MCP process role. These registrations use the same loaded plugin catalog. See [Compiled Plugin Settings](references/developer-guide-plugins-compiled-settings.md).

## Related articles

- [IAgentPlugin](references/developer-guide-plugins-iagent-plugin.md)
- [Plugin Context](references/developer-guide-plugins-context.md)
- [Plugin Credentials](references/developer-guide-plugins-credentials.md)
- [Toolsets](references/developer-guide-toolsets-toolsets.md)

## Instruction defaults

Use `AddInstruction("browser-use", "Browser Instruction", defaultMarkdown)` to declare a default instruction. Keys contain lowercase ASCII letters, digits, and hyphens and are unique within the plugin. The label and default text must be nonempty. Loaded contributions are exposed through `LoadedAgentPlugin.Instructions`. The Host qualifies each key with the plugin ID and binds current defaults during user-scope composition. The Browser plugin supplies its existing instruction topic through this declaration.

This declaration supplies data. The Host owns user modes, encrypted files, editing, and consumption. `AddAgentTemplate` and `AddTask` remain the creation paths for starter agents and provisioned tasks. Installed starter agents retain their last supplied default when a plugin disappears. Available template updates refresh that default while preserving the user's mode, custom text, and other settings.

## Task contributions

Use `AddTask` to supply initial task settings and, when needed, one execution handler factory. The host owns scheduling, user edits and run history. Your definition factory returns a fresh `AgentTaskDefinition` for each user. The host assigns its user, agent, identity and creation time.

```csharp
builder.AddTask(
    "daily-check",
    context => new AgentTaskDefinition
    {
        Name = "Daily check",
        Instruction = "Review the day's notes and report anything needing attention.",
        Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.Cron,
            CronExpression = "0 9 * * *",
            TimeZoneId = context.UserTimeZone.Id
        }
    });
```

Keys contain lowercase ASCII letters, digits or hyphens and must be unique within the plugin. Registration owns the key. The key is the saved task's permanent identity: renaming it in a later release leaves tasks seeded under the old key without their plugin. A release may add, change or remove the task's handler, and existing tasks pick it up after restart. Use the defaults-only overload, the `deterministicHandlerFactory` overload for `IDeterministicTaskHandler`, or the `agentHandlerFactory` overload for `IAgentTaskHandler`. The handler factory receives that user's `AgentPluginContext` under the task's privacy policy and creates a fresh handler per run. The Host disposes handlers implementing `IAsyncDisposable` or `IDisposable`.

Defaults are seeded only when the task is missing during user provisioning. Existing settings are preserved. Disabling a plugin takes effect after restart and leaves saved tasks and history intact. Its tasks then record **Skipped** with a reason, whatever their instruction mode, until the plugin returns. Deleting a seeded task removes its saved instance, and a later provisioning pass may recreate it while the plugin is active.

Either handler can throw `AgentTaskSkippedException` from `Oscova.Agents.Tasks` to report an expected skip. Supply a localization key and a fallback reason. Its optional `priority` defaults to `NotificationPriority.Normal` from `Oscova.Agents.Notifications`. Select `Low` for a particular skip that should reach the inbox without an arrival toast:

```csharp
using Oscova.Agents.Notifications;
using Oscova.Agents.Tasks;

throw new AgentTaskSkippedException(
    "DailyCheck.SetupRequired",
    "Configure the daily check before running this task.",
    NotificationPriority.Low);
```

The run records **Skipped** and its reason. Oscova retains the existing task/run-linked **Needs attention** notification at either priority. Low affects the arrival toast, while sounds and muting follow the user's settings. Exceptions representing actual failures keep the ordinary error notification. Lower priority at the specific prerequisite check rather than applying it to every exception.
