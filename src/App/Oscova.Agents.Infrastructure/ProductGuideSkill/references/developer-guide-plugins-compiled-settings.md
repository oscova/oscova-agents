# Compiled plugin settings

A plugin can supply independent settings pages or a compiled component in an existing host slot. Independent pages own their configuration, validation and storage choices. Model-provider panels edit the host's unsaved provider draft and use its atomic encrypted save. Oscova supplies navigation, authenticated capabilities, optional storage services, dialogs and the installed theme.

## Register a component

Use a .NET 10 Razor class library with `Microsoft.AspNetCore.App` and a compile-only reference to the matching `Oscova.Agents` package. Register through `AgentPluginBuilder.AddSettingsPanel(AgentSettingsContribution)`. Each panel ID contains one or more lowercase ASCII letters, digits or hyphens. IDs must be unique within your plugin. Different plugins can use the same panel ID.

| Destination | Association | Placement |
|---|---|---|
| `PluginSettingsSlots.Page` | `null` | An independent page within its declared host settings category |
| `PluginSettingsSlots.ExecutionEnvironment` | Your registered execution provider ID | The selected provider's region in Execution settings |
| `PluginSettingsSlots.SpeechRecognition` | Your admitted recognition provider type | The selected provider's controls in Speech settings |
| `PluginSettingsSlots.SpeechSynthesis` | Your admitted synthesis provider type | The selected provider's controls in Speech settings |
| `PluginSettingsSlots.ModelProvider` | Your admitted model provider type | Advanced options in the provider editor |

Independent pages require `PluginSettingsPage` metadata. Inline panels require an owned association and do not accept page metadata. The component must be concrete and implement Blazor `IComponent`. It exposes a public `[Parameter]` named `Context`. The factory result must be assignable to that parameter. You can supply a typed plugin-owned context.

```csharp
builder.AddSettingsPanel(new(
    "settings", PluginSettingsSlots.Page, null,
    typeof(EchoChannelSettingsComponent), context => context,
    PluginSettingsAccess.CurrentUser)
{
    Page = new("workspace", "Echo", "Plugin-owned settings.")
});
```

`PluginSettingsPage` includes optional title and description localization keys and an embedded SVG resource name. Existing `Help` and `RelatedHelp` declarations remain on the panel. Help-topic IDs share a plugin-wide namespace, so use distinct IDs for different help content across your pages.

Oscova supports these category identifiers. Other hosts decide which identifiers they support.

| Category ID | Current Oscova label |
|---|---|
| `workspace` | Preferences |
| `intelligence` | Agents & AI |
| `connections` | Accounts & Access |
| `channels` | Channels |
| `data` | Files & Backup |

Independent pages use `/settings/{categorySlug}/plugins/{pluginId}/{panelId}`, with plugin and panel identities encoded as URL segments. The registry resolves only admitted pages and their declared category. Labels can change independently of category identity. Built-in entries precede plugin pages, which are ordered by plugin ID and panel ID.

Unknown destinations or categories, duplicate identities, foreign associations, invalid component types and context mismatches produce diagnostics. Other valid contributions remain available. Channels explicitly register a settings page when they need one. A runtime-only channel receives no generated form.

## Use the authenticated context

`PluginSettingsContext` carries the owning plugin identity, optional inline target identity, `AgentPluginContext`, optional-use bound `Settings`, narrow `Ui` operations, current access admission and current offline state. Page routes and host-supplied operations enforce vault, plugin activation and current-user or operator access. Compiled plugins are trusted .NET code, and these capabilities do not isolate arbitrary plugin IO.

You choose where and how to persist your model. `AgentPluginContext.UserDataDirectory`, `UserSettingsDirectory` and `PrivateDataDirectory` supply contextual locations. `UserSettings` optionally exposes the same current-user plugin namespace to both the page and runtime. No serializer, filename, extension, storage root or encryption policy is mandatory for a plugin.

For the supplied `Settings` and `UserSettings` stores, local keys contain only ASCII letters, digits and hyphens. These stores bind the user and plugin namespace and recheck admission on reads and writes. Oscova encrypts current-user bound settings. Operator-panel `Settings` uses shared installation storage and checks current operator authority. Runtime `InstallationSettings` supports owning-plugin reads and rejects writes. Docker's shared nonsecret Dockerfile configuration and generated build inputs remain plaintext.

`Services.Encryption` supplies optional current-user, plugin-bound encryption. Official per-user channel plugins choose encrypted YAML in the user settings folder. Their bot tokens continue in declared credential storage. Echo's independent page deliberately saves its own plaintext text file to demonstrate that using the supplied store is optional.

`Ui.EditTextAsync(title, load, save, helpTopic)` opens the existing editor using your callbacks. `Ui.ConfirmAsync(title, message)` requests confirmation. `Ui.Notify(message, kind)` reports a semantic notification. No private Host component or service-provider reference is required.

Retain a `RegisterOperation` registration for long device operations to receive authority-change cancellation. Release subscriptions and owned resources when your component closes. The renderer disposes disposable context values. Closing a page does not stop an independently running channel.

## Edit a model provider

Register a `ModelProvider` panel with the owned provider's `Type` and `PluginSettingsAccess.CurrentUser`. Only the successfully admitted runtime contribution can supply that provider's panel. Shared consumers cannot edit the owner's options.

Call `PluginSettingsContext.GetModelProviderSettings()` for the current `PluginModelProviderSettingsContext`. Edit `Draft.Options`, then await `ChangedAsync()` to refresh validation and estimates. Read `FieldErrors` for current validation messages. Keep options in this draft, with no separate storage write. Save persists them with the owner's encrypted provider record. Cancel discards changes, Duplicate clones them, and a provider-type change clears them.

Implement `IModelProviderConfiguration.ValidateConfiguration` for side-effect-free option validation and `ReleaseConfigurationAsync` to retire per-definition resources after the host drains executions. Do not initialize native models from rendering or validation. Import `Microsoft.AspNetCore.Components.Web` in Razor controls so event directives bind to their handlers. Localize labels and errors through the plugin's `UseResources<T>()` registration.

## Edit a speech provider

Register a panel for each speech capability your plugin supplies. Use the provider's `Type` as its association and `PluginSettingsAccess.CurrentUser` as its access scope. The host admits only panels whose associated runtime provider belongs to your plugin, separately for recognition and synthesis.

Read `PluginSettingsContext.GetSpeechSettings()` from your component to obtain the current `PluginSpeechSettingsContext`. It supplies the unsaved `Draft`, default model, saved credential choices without secret values, available voices and voice readiness. Update the draft's credential or model and call `ChangedAsync()` to refresh the page. Use `SelectVoiceAsync(voiceId)` for voice changes. The host's **Save speech settings** action commits the drafts through its existing user settings storage.

`ModelDownload` is available only for a selected, saved provider with local model assets. It exposes readiness, progress, Start, Cancel and a disposable status subscription. Recheck `RequireAccessAsync` before invoking download actions and dispose subscriptions when your component closes. The host retains download admission and lifecycle.

Keep provider-specific fields and `UseResources<T>()` localization in your plugin. The official OpenAI, ElevenLabs and local speech plugins reuse linked plugin-side controls while supplying their own compiled components and resources. Speech execution continues to receive a resolved credential only for the current call. Declare a credential when the provider requires one that the host does not already supply. Synthesis draft changes refresh voice discovery with the unsaved configuration, while Save remains host-owned.

## Control an owned channel

Resolve `IPluginChannelControl` through `PluginSettingsContext.ResolveChannelAsync(channelId, cancellationToken)`. The channel must be declared by the same plugin. The capability exposes observed status, available agent choices, agent validation, Start, Stop, applying independently saved configuration and coordinated Save/application.

`SaveAsync(agentId, persist, cancellationToken)` validates the agent and runs your asynchronous persistence callback under the channel's lifecycle gate. The callback receives whether the channel is running and requires complete configuration. Combine this with your draft's startup intent when validating. The callback owns model validation, serialization and atomic persistence. It must not reenter channel-control operations while the gate is held.

Persistence failure preserves the old durable model and active connection. After a successful write, a running channel reloads its plugin snapshot before Stop and replacement. A read failure reports saved-but-not-applied and retains the old connection. A restart failure reports separately from save success. Stopped channels remain stopped. Start always loads fresh validated configuration.

Observed status and Stop do not read configuration. Keep those actions available independently of settings initialization so a corrupt file does not prevent shutdown. Save unsaved edits before enabling Start.

`SubscribeAsync(observer, cancellationToken)` admits the subscription and returns a disposable current-user subscription. Subscription admission and notification delivery recheck authority. The observer receives complete immutable status and must return immediately without blocking or reentering channel controls. Enqueue a Blazor dispatcher update, return from the observer, and use the supplied payload without querying status again. Dispose the subscription and ignore pending dispatcher updates after navigation.

## Use the installed appearance

Use classes present in the installed Host theme. The contract has no stylesheet, script, asset bundle or runtime class scanner. Register plugin localization with `UseResources<T>()`. The Host resolves your embedded SVG and Markdown help using its existing validation and resource limits. Missing optional icons use the generic settings icon.

Run `samples/Oscova.Agents.PluginSettingsPreview` in the public `Oscova.Agents` repository for appearance checks in light and dark themes. Its sample runtime is separate from installed-host authorization, persistence and real external connectivity.

## Register execution behavior

`AddExecutionProvider(AgentExecutionContribution)` supplies a stable nonempty ID, presentation and explicit capability metadata, plus a runtime factory. IDs are case-insensitive and `host` is reserved. Null operator selection means Disabled. The selection applies to every account. An unavailable provider is an error and never runs a Host command instead.

Implement `CheckAvailabilityAsync(ExecutionAvailabilityRequest, CancellationToken)` to check prerequisites on the executing device. The request supplies the effective network permission and sanitized environment. Return `ExecutionAvailabilityResult` with availability, actionable fallback text and an optional plugin localization key. Selection and authoritative Save both call this operation. A connected node checks locally before advertising readiness. Do not probe in the provider constructor or install dependencies during a check.

`ExecutionEnvironmentDefinition` declares host-path use, offline support, node support and optional explicit Host-command support. The provider implements `IExecutionEnvironmentProvider`. Each session exposes the existing Microsoft `ShellExecutor`, script-path mapping, foreground recovery and backend background jobs. The Host retains command admission, approval, run ownership, public job IDs, byte limits and permission cancellation. Returning false from recovery requests session replacement.

`AddContainerLauncher(AgentContainerLaunchContribution)` supplies the single containerized stdio MCP launch role. Its launcher returns standard process values after readiness. MCP transport and process ownership remain with Oscova. Multiple admitted launchers make that role unavailable. There is no load-order selection or native fallback.

Oscova creates a runtime once per active device catalog, without process probes during registration. A plugin can close execution, container launch and panel factories over the same runtime. Set node support only when the capability works with device-local state. Nodes prepare the exact plugin package through the existing installer. Operator Dockerfiles, credentials and private runtime settings are not copied to a node.

`IExecutionEnvironmentProvider.StopAsync()` cancels and awaits shared preparation while Oscova blocks new work for a configuration change. It must allow later reuse after re-enabling the plugin. Ordinary run cancellation may stop only that run's wait for shared preparation. Provider deactivation must stop the preparation itself, including on connected nodes, before the settings change completes. Node disconnection also stops device-local preparation. Final disposal releases the runtime's resources.

Background handles complete `IExecutionBackgroundJob.Completion` when their command ends, without waiting for another tool call. The registry then releases permission and configuration ownership and retains at most 25 completed jobs. Backends release completed process resources while preserving their final status and buffered output.

## Related articles

- [Plugin Builder](references/developer-guide-plugins-plugin-builder.md)
- [Plugin Context](references/developer-guide-plugins-context.md)
- [Echo Plugin](references/developer-guide-examples-echo-plugin.md)
- [Execution settings](references/user-guide-settings-and-admin-execution.md)
