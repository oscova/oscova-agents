# Plugin context

`AgentPluginContext` is the supported bridge between the Host and plugin contribution factories. It provides credential resolution, policy-guarded HTTP transport, logging, time-zone information, tracked asset downloads, the plugin deployment directory, and the Host-owned model storage root without exposing the Host service provider.

> **Note:**
> Toolset factories receive a user-scoped context. Model and speech provider factories receive a startup context with HTTP, logging, and tracked-download capabilities, but its credential resolver always returns `null`.

## Use the supplied capabilities

| Member | Capability |
|---|---|
| `ResolveAccessTokenAsync(providerId, cancellationToken)` | Resolves a usable token for an account declared by this plugin in the current user scope. Unsupported hosts return null. |
| `CredentialResolver` | Resolves only credential keys declared by this plugin's registered contributions in a user scope. Provider contexts expose no credential values. |
| `CreateHttpClient()` | Creates a Host-managed `HttpClient` subject to the current run policy. |
| `Services.CreateHttpMessageHandler()` | Creates a guarded HTTP handler for an SDK that builds its own client. |
| `Services.ToolOutputStore` | Saves a large tool result in the current run for later reading or searching. It returns no file when the host or run has no output store. Use `ToolOutput.LimitText` for bounded inline text. |
| `LoggerFactory` | Creates loggers that use the host logging pipeline. |
| `TimeProvider` and `UserTimeZone` | Supply current time and the user's selected time zone in a toolset context. |
| `PluginDirectory` | The read-only directory containing the current plugin's manifest and deployed files. |
| `ModelStorageDirectory` | The host-owned writable root under which providers can create their own model directory. |
| `EnsureDownloadAsync(...)` | Starts or joins a host-tracked asset download using the existing progress and cancellation services. |
| `UserDataDirectory` | The current user's data root, or null in a context without a user. |
| `UserSettings` | Optional current-user settings storage bound to the owning plugin, shared by runtime and settings components. |
| `UserSettingsDirectory` | The current user's settings directory, or null in a context without a user. |
| `InstallationSettings` | Optional owning-plugin installation settings. Runtime contexts allow reads and reject writes. |
| `PrivateDataDirectory` | Optional writable plugin directory on the execution device, separate from user storage. |
| `GetAllowedDirectories()` | Resolves the active invocation's authorized directories on its execution device. Read this during the tool call, not once during construction. |
| `Services.Encryption` | Optional-use text encryption for the current user and plugin. A custom host without this capability may throw `NotSupportedException` when it is requested. |

Register a factory in `IAgentPlugin.Configure` and pass only the capabilities the toolset needs:

```csharp
public void Configure(AgentPluginBuilder builder)
{
    builder.AddToolset(context => new NewsToolset(
        context.CredentialResolver,
        context.CreateHttpClient(),
        context.LoggerFactory.CreateLogger<NewsToolset>(),
        () => context.UserTimeZone));
}
```

Provider factories use the same context shape for host-managed HTTP and logging:

```csharp
builder.AddModelProvider(context => new ExampleModelProvider(
    context.CreateHttpClient(),
    context.LoggerFactory.CreateLogger<ExampleModelProvider>()));
```

Provider operations receive any selected plaintext API key through the existing `apiKey` parameter on the provider interface. Do not resolve user credentials in a provider factory.

Treat `PluginDirectory` as deployment content. Do not write models, settings, or caches beside plugin binaries. Create a provider-specific child directory under `ModelStorageDirectory` instead of inferring the host's application-data layout.

Pass the provider-owned destination path to `EnsureDownloadAsync(...)` when an asset is missing. Oscova remains responsible for tracking progress and cancellation. The plugin supplies only the stable operation ID, source URL, destination path, and cancellation token. The destination must be fully qualified and remain under `ModelStorageDirectory`, otherwise the context throws `ArgumentException` before starting a download. Direct calls through `Services.EnsureDownloadAsync(...)` enforce the same destination requirements.

`UserTimeZone` resolves the current preference each time you read it. A long-lived toolset that formats user-facing timestamps should accept a `Func<TimeZoneInfo>` and invoke it when needed, as in the factory above. Capturing one `TimeZoneInfo` during construction would keep the old preference after the user changes it.

`CreateHttpClient()` preserves Host transport policy. In a local-only task, it blocks public egress even when a plugin's factory or handler attempts the request. An SDK that constructs its own client can use `Services.CreateHttpMessageHandler()` to keep the same guard. A channel connection instead receives its connection-owned vendor transport through `ChannelConnectionContext.Transport`.

## Store private plugin data

Plugins own their file reads and writes. Use `UserDataDirectory` or `UserSettingsDirectory` when you want to store a file for the current user. Choose a distinct plugin-owned filename or subdirectory to avoid overwriting application files. These paths describe storage locations and do not restrict ordinary file IO.

Call `Services.Encryption.Encrypt(text)` before saving private text, and call `Decrypt(ciphertext)` after reading it back. Both methods are synchronous and return a string. Oscova supplies the user and plugin identity automatically. Your plugin never receives a key or passes a user ID.

The following method can be called from a user-scoped plugin contribution:

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Oscova.Agents.Plugins;

public static async Task SavePrivateTextAsync(
    AgentPluginContext context, string text, CancellationToken cancellationToken = default)
{
    var root = context.UserDataDirectory
        ?? throw new InvalidOperationException("Plugin storage requires a user context.");
    var ciphertext = context.Services.Encryption.Encrypt(text);
    var directory = Path.Combine(root, "my-plugin");
    Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(Path.Combine(directory, "state.txt.enc"), ciphertext, cancellationToken)
        .ConfigureAwait(false);
}
```

Read the ciphertext using ordinary file IO, then pass that string to the same plugin's `Decrypt` method. Its purpose is a stable label bound to the plugin identity. Decryption requires the same user vault and plugin identity, including after a restart. Renaming the plugin ID changes that identity. Oscova keeps no ledger of encrypted output or plugin files.

An unavailable user context throws `InvalidOperationException`. A locked vault throws an `InvalidOperationException` identifying the Vault Password requirement. Damaged ciphertext or ciphertext belonging to another user or plugin throws `System.Security.Cryptography.CryptographicException`. Decrypt returns plaintext or throws, never null. A retained service observes vault lock and unlock on each call.

Tool, task and channel contexts receive their user's paths and encryption identity. Startup model and speech provider contexts have null user paths, and attempting encryption or decryption there throws a user-context error.

The service is optional to use and does not encrypt arbitrary file writes automatically. It opens the plugin's own ciphertext, not the harness's settings files or another plugin's ciphertext. General encrypted-file readers use a different purpose and cannot open these plugin files. This capability does not sandbox trusted plugin code or restrict its ordinary file access.

## Follow contribution lifetimes

The plugin entry point registers contribution factories once when the assembly loads. Oscova creates one provider context per loaded plugin and shares it among that plugin's provider factories. For each user scope, Oscova creates a separate context per plugin and shares it among that plugin's toolset factories.

- Do not store `AgentPluginContext` or a toolset instance in static state.
- Do not share a context between users.
- Do not share a context between plugins.
- Resolve credentials when the tool runs so rotations and missing values are observed.
- Keep the factory quick and free of remote calls or background work.
- Retain only the supplied client, logger, resolver, or time-zone delegate needed by that toolset.
- A provider or toolset can implement `IAsyncDisposable` or `IDisposable` for owned resources. Oscova releases it when its Host lifetime ends, or earlier if activation fails.

The context is a capability boundary, not a process or security boundary. Plugin code remains trusted in-process code and can use .NET APIs outside the context. Enabling a plugin therefore requires the same trust review described in [Plugin Manifest](references/developer-guide-plugins-manifest.md).

## Local diagnostic context

Providers can opt into `ILocalModelDiagnosticProvider` for a bounded isolated load and short inference. The child activates only the admitted installed provider contribution, with a sanitized configuration containing no credentials. Its context supplies paths, logging and clock access. Network clients, downloads, OAuth, credentials, encryption and user storage are unavailable. Unsupported service requests fail. This contains native crashes but does not sandbox trusted plugin code.

Use `IModelHardwareProvider.GetHardwareProfile` for a side-effect-free placement description. Host inventory remains in the host, and estimates must not initialize a runtime. Configuration options persist facts chosen by the user, while detected hardware and diagnostic results remain transient.

## Resolve credentials safely

`IToolCredentialResolver` returns `null` when a credential or field is unavailable or the requested key was not declared by that plugin's registered toolset and provider types. An undeclared key is rejected before the credential store is queried. Both overloads accept cancellation:

```csharp
Task<string?> ResolveAsync(
    string credentialKey,
    CancellationToken ct = default);

Task<string?> ResolveAsync(
    string credentialKey,
    string fieldName,
    CancellationToken ct = default);
```

Use the first overload for a single-field credential and the second for a named field. Never put resolved secret values in logs, exceptions, or tool results. Return a clear setup message when a required value is missing.

## Use a connected account

Declare the provider with `AddOAuthProvider`, then resolve a usable token at tool-call time:

```csharp
var token = await context.ResolveAccessTokenAsync("google", cancellationToken)
    .ConfigureAwait(false);
```

This example belongs in a Google Workspace tool. A Microsoft Graph tool requests its own `microsoft` provider ID through the same delegate. Oscova rejects another plugin's provider before reading credentials. It supplies only the current user's token, never a caller-selected user, refresh token, or shared app secret. A missing connection returns null. Cancellation and account-access failures propagate, and offline policy is enforced by the host. Do not log, return, or cache the token.

`ResolveAccessTokenAsync` delegates to the supplied `IAgentPluginServices`. A custom host implements that service and must enforce user and plugin ownership itself, returning null when account support is unavailable. Oscova's startup contexts return null for a provider owned by the plugin because no user token service is present. The application owns consent and refresh.

## Handle initialization failures

If a toolset factory throws, that contribution is not added to the affected user's tool collection. This is separate from manifest loading, so validate normal user setup, credentials, and remote availability when the tool runs instead of throwing from the factory.

Use `LoggerFactory` for operational failures and include non-secret context such as the plugin, tool, credential key, or remote service. Pass the tool call's cancellation token to credential and network operations.

## Construct a context in tests

Tests and custom hosts can create a context directly. The constructor requires a credential resolver, guarded Host services, logger factory, time provider, and both fully qualified directory paths:

```csharp
var context = new AgentPluginContext(
    credentialResolver,
    pluginServices,
    loggerFactory,
    timeProvider,
    pluginDirectory,
    modelStorageDirectory);
```

The supplied `IAgentPluginServices` implementation must enforce account ownership and transport policy. There is no unguarded convenience constructor.

## Troubleshooting

- **Credential is always null:** verify the credential key is declared directly on a registered contribution, then check the field name and resolve it at call time for the current user.
- **Requests are blocked:** check the current run's network policy and use `CreateHttpClient()` or `Services.CreateHttpMessageHandler()` for SDK transport.
- **Tool is missing for one user:** make sure the factory does not throw while that user's tool collection is assembled.
- **Times use the server zone:** use `context.UserTimeZone` instead of `TimeZoneInfo.Local` in production toolsets.
- **Tracked downloads are unavailable:** use the full constructor in a custom host and supply its download operation.

## Settings and execution contributions

Interactive settings factories receive `PluginSettingsContext` with authenticated, plugin-bound storage and UI operations. Device runtime contexts expose read-only `InstallationSettings` and a writable `PrivateDataDirectory`. See [Compiled Plugin Settings](references/developer-guide-plugins-compiled-settings.md) for scope, authority, lifetime and preview contracts.

## Related articles

- [Plugin Credentials](references/developer-guide-plugins-credentials.md)
- [Plugin Builder](references/developer-guide-plugins-plugin-builder.md)
- [Echo Plugin](references/developer-guide-examples-echo-plugin.md)
