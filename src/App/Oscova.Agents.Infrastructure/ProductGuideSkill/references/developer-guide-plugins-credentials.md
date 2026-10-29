# Plugin credentials

Use `CredentialSlotAttribute` to publish a single-field API-key slot for a registered plugin toolset or provider. Toolsets resolve the current user's value through `IToolCredentialResolver`. Model and speech providers receive the resolved value through their provider interface.

> **Note:**
> A credential declaration describes what users can configure. It never contains the secret itself.

Speech providers can consume an existing host-declared credential without declaring another slot. The host resolves the authenticated user's selected `SpeechProviderConfig.CredentialKey` and passes the resolved string as the operation's nullable `apiKey` argument. A provider factory receives no user key. Local providers accept the same argument and ignore it.

The OpenAI speech plugin uses this path to reuse the host's `openai` credential declaration. It does not call `AddCredential` or directly look up that host key. Declaration-scoped resolver lookup and host-supplied provider arguments are separate paths. Plugins run as trusted code in the host process, so a resolved key is accessible to the provider receiving it. Do not retain it in shared provider state, persist it, or put it in logs or tool results.

## Declare a credential slot

Apply one `CredentialSlotAttribute` directly to a toolset, model provider, speech recognition provider, or speech synthesis provider class that the plugin registers:

```csharp
using System.ComponentModel;
using Oscova.Agents;
using Oscova.Agents.Credentials;

[CredentialSlot(
    Key = "example-api",
    Label = "Example API",
    Description = "API key used by the Example tools.",
    EnvVariable = "EXAMPLE_API_KEY")]
public sealed class ExampleToolset(
    IToolCredentialResolver credentialResolver) : IAgentToolset
{
    public const string CredentialKey = "example-api";

    [DisplayName("example_check_configuration")]
    [AgentTool]
    [Description("Checks whether the Example API credential is available.")]
    public async Task<string> CheckConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        var apiKey = await credentialResolver
            .ResolveAsync(CredentialKey, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(apiKey)
            ? "Example API is not configured. Add the Example API credential in Settings > Accounts & Access > Credentials."
            : "Example API is configured.";
    }
}
```

The declaration is discovered only when the plugin is enabled and the declaring type is registered. An attribute on an unrelated class is ignored.

The properties are:

- `Key`: the stable lookup key. Use a short lowercase service-specific value.
- `Label`: the name shown in **Credentials**.
- `Description`: one-line setup guidance.
- `EnvVariable`: an optional environment-variable name suggested to the user.

`Key`, `Label`, and `Description` must contain non-whitespace text. An invalid slot is reported as a diagnostic without hiding other valid plugin slots.

Each attribute declaration creates one required, secret field named `apiKey` in the current user's credential scope. `CredentialSlotAttribute` does not support custom fields or multiple fields.

## Keys and duplicate declarations

Credential keys are matched after trimming and lowercasing within the declaring plugin's namespace. Two contributions in one plugin may intentionally share one key, but another plugin or the Host can use that same local key without sharing a stored value.

Do not rely on declaration scan order. The first valid declaration within one plugin supplies the label, description, and suggested environment variable for a shared local key.

Avoid accidental collisions. If several contributions share a credential, define the key once as a constant and use identical metadata on every declaration.

## Declare structured fields

Use `AgentPluginBuilder.AddCredential` for channels or other contributions that need custom field names or multiple values. Pass a `CredentialDeclaration` from `Oscova.Agents.Credentials`, with a stable key, label, scope, and named `CredentialFieldDefinition` entries. Each field declares whether it is required and secret, and may suggest an environment-variable name. The declaration contains metadata only.

For example, the Discord plugin declares `discord-bot` with one required secret field named `botToken`. Its connection resolves that field through its user-scoped `AgentPluginContext`. The same declaration drives the existing credential editor, without adding plugin UI or storing secret values in channel settings.



## Declare a connected account

Use `AddOAuthProvider` with `OAuthProviderDefinition`. It registers both the account declaration and its `AppCredential`. Oscova supports one connected account per provider per user.

- Set a unique lowercase `Id` containing only ASCII letters, digits, or hyphens, plus a nonempty `DisplayName`. `Services` supplies the card subtitle and `Description` supplies setup guidance. Text is rendered as text.
- Set absolute HTTPS `AuthorizationEndpoint`, `TokenEndpoint`, and `UserInfoEndpoint` values without embedded credentials or fragments. Supply nonempty `Scopes` and a pure `ReadConnectedEmail` delegate. The delegate reads the provided `JsonElement` during the call and returns an email or null.
- Set `AppCredential.Key` to `oauth-provider:{id}`, a nonempty label, and `CredentialScope.System`. Supply exactly two required fields: `OAuthProviderDefinition.ClientIdField` as non-secret and `OAuthProviderDefinition.ClientSecretField` as secret.
- Optional `AuthorizationParameters` cannot override `client_id`, `client_secret`, `redirect_uri`, `response_type`, `scope`, or `state`.
- Duplicate or invalid declarations are rejected. The first persisted plugin owner of a provider ID keeps that identity across restarts and secret clearing. Another plugin cannot claim it.

The operator configures the shared client ID and secret. Each user connects through **Connected Accounts**. Oscova owns consent, encrypted tokens, expiry, refresh, and local disconnect. Plugins resolve only usable access tokens through [Plugin Context](references/developer-guide-plugins-context.md). The general credential resolver denies managed `oauth:` entries even if a plugin declares their keys.

Disabling a plugin retains its credentials and connected-account tokens. Explicit uninstall removes that plugin's stored credentials and tokens for all users while leaving other plugin and Host namespaces intact. Refreshing the same package does not clear its namespace.

This contract supports the existing authorization-code flow. It does not provide device-code flows, API-key account cards, multiple accounts, or cross-plugin account sharing.

## Resolve a toolset credential

Keep the resolver supplied through `AgentPluginContext` and resolve the credential when an operation runs:

```csharp
var apiKey = await credentialResolver
    .ResolveAsync(ExampleToolset.CredentialKey, cancellationToken)
    .ConfigureAwait(false);
```

For a declared plugin slot, the single-field overload is the normal choice. The named-field overload is equivalent when called with `"apiKey"`:

```csharp
var apiKey = await credentialResolver.ResolveAsync(
    CredentialKey,
    "apiKey",
    cancellationToken);
```

Credential keys are normalized, but field names are exact and case-sensitive. The resolver returns `null` when the credential, field, stored value, referenced environment variable, or stored value's unlocked user key is unavailable. Treat a locked credential exactly like other missing setup. Cancellation and unexpected storage failures can propagate to the caller.

## Receive a provider credential

Provider factories receive an `AgentPluginContext` for HTTP and logging capabilities, but that context does not expose user credential values. When a model or speech configuration selects a credential, Oscova resolves it for the current user. Provider operations that need credentials receive the plaintext value through their `string? apiKey` parameter.

The attribute publishes the slot but does not automatically select it in a provider configuration. Treat a null or blank `apiKey` as missing setup and follow the failure contract of the corresponding `IModelProvider`, `ISpeechRecognitionProvider`, or `ISpeechSynthesisProvider` method.

## Storage and trust boundaries

Attribute-declared slots belong to the current user. Structured declarations can also request system scope for supported shared configuration. A stored secret is encrypted through that user's vault and is not displayed again. After restart it resolves as unavailable until the user unlocks with their Vault Password. An environment-backed slot remains available while the vault is locked: it stores only the variable name and reads its value from the Oscova host process when the resolver is called.

Only the operator can reference the server's environment variables, including through shared system credentials. A regular user must store a personal credential value. An unauthorized environment lookup throws `UnauthorizedAccessException` instead of returning a secret or treating the variable as missing.

The resolver returns plaintext to the trusted plugin operation. Keep that value in the smallest possible scope:

- Do not store it in plugin files, settings, static fields, or long-lived caches.
- Do not include it in tool arguments, results, exceptions, URLs, or logs.
- Send it only to the intended service, preferably in an authorization header.
- Resolve it again on the next operation so user rotation takes effect.

The supported toolset resolver accepts only keys declared directly by that plugin through registered toolset/provider attributes or `AddCredential`. Requests for other keys return `null` without reading credential storage. This prevents accidental cross-plugin access through the framework context, but it is not hostile-code isolation. Plugins remain trusted in-process code, so administrators must install only plugins they trust.

Oscova applies redaction to known user secrets and secret-shaped tool arguments at execution boundaries, but redaction is defense in depth. It does not make logging or returning a secret safe.

## Failure guidance

- Missing value: name the slot and direct the user to **Settings > Accounts & Access > Credentials**.
- Unset environment variable: ask the user to verify the variable in the host process environment.
- Unauthorized provider response: say that the configured credential is invalid or expired without echoing the response body.
- Cancellation: allow `OperationCanceledException` to propagate.
- Remote or parse failure: log service and operation context, never the credential value.

## Related articles

- [Plugin Context](references/developer-guide-plugins-context.md)
- [Plugin Builder](references/developer-guide-plugins-plugin-builder.md)
- [Toolsets](references/developer-guide-toolsets-toolsets.md)
- [Credentials](references/user-guide-settings-and-admin-credentials.md)
