# Credentials

Credentials let you save a secret once and select it from model, embedding, speech, channel or tool settings. Credentials belong to the signed-in user, and stored values are encrypted under your Vault Password.

> **Note:**
> A credential is a saved secret or a reference to a host environment variable, such as an API key or bot token.

## Understand the two lists

- **Integration credentials** are named slots declared by built-in features, providers, toolsets or enabled plugins that load successfully. Examples include provider API keys and the Discord and Telegram bot tokens. Their names and required fields are fixed because the owning feature looks them up by a stable key.
- **Your credentials** are free-form entries you create for providers or other settings that accept a saved credential.



A row shows **Set** when it has a stored row and every required field contains either an encrypted stored value or an environment-variable name.

> **Important:**
> For an environment-backed field, **Set** confirms only that a variable-name reference exists. Oscova does not check that the host process currently has a value for that variable until a feature tries to use it.

Managed OAuth tokens are not shown. Connected Accounts creates, refreshes and deletes those tokens through its own workflow.

OpenAI models and the optional **Oscova OpenAI** speech plugin can use one saved OpenAI credential. Select that credential independently in the model configuration and both Speech provider configurations, then save. The speech plugin does not add a second OpenAI credential slot or inherit a model's selected key.

## Add a free-form credential

1. Select **Add Credential**.
2. Enter a unique **Name**. It must contain at least one letter or digit. Oscova turns it into a stable key, so a name is rejected if its key is already used or reserved by an integration slot.
3. Optionally add a **Description** that identifies the intended service or account.
4. Choose a source for **Value**: **Enter a value** stores the secret encrypted, or **Use an environment variable** stores only the variable name and reads its value from the host process at runtime.
5. Select **Save Credential**.

- The environment-variable source is available only to the operator. The field suggests names visible to the running Host, and the operator can type another name for a future restart. Regular users enter and store their own values and cannot list or reference server environment variables.
- A stored value requires your vault to be unlocked. After a restart, Oscova asks for your Vault Password before authenticated pages load. Environment-backed fields do not require an unlocked key because Oscova persists only the variable name.

For example, create a credential named `Internal Search` and select it from the provider or tool setting that needs that secret. If the entry uses `INTERNAL_SEARCH_KEY`, that variable must be available to the Oscova host process.

## Fill an integration credential

Select **Edit** beside the required integration. The dialog shows the label the feature needs, such as **API Key**, **Bot Token** or **Personal Access Token**.

- Some slots suggest a default environment-variable name. You can keep it, enter another variable name or switch to a stored value.
- Integration credential names and descriptions are read-only, and the rows cannot be deleted from this page. Edit the row to replace its value or change its source.

## Keep and rotate secrets

Stored secrets are encrypted at rest with a per-user data key and never displayed again. Only a Vault-Password-wrapped copy of that key is stored in the database. After a Host restart, the secret resolves as unavailable until its owner unlocks the vault, and scheduled tasks wait while the vault is locked.

To rotate a stored secret:

1. Select **Edit**.
2. Select **Enter a value**.
3. Enter the replacement. Leaving the value blank preserves the current stored secret.
4. Select **Save Credential**.

- Saving a credential selected by a model provider refreshes cached model clients so later calls use the replacement. A request already in progress may still use the old value.
- For an environment-backed credential, update the variable in the environment that launches Oscova. Restart the host if that environment is captured only when the process starts.
- If a feature reports that a credential is unavailable, check both the saved variable name and the actual runtime environment value.

## Rename or delete a free-form credential

Editing a free-form credential name can change its generated key. Oscova moves user-scoped references in model-provider, selected embedding-provider, speech-recognition and speech-synthesis settings to the new key. If those reference updates cannot be saved, the rename fails and the original credential remains usable.

Only free-form credentials have **Delete**. Deletion is blocked while the credential is selected by a model provider, the selected embedding provider, a speech-recognition provider or a speech-synthesis provider. Remove the listed references, then retry.

User credential deletion does not check administrator-owned hook secrets. Hook credentials use a separate system credential store, even if a key happens to have the same text.

## Troubleshooting

- **A credential name is required:** enter a non-empty name.
- **The name must contain a letter or digit:** use a name that can produce a key.
- **Enter a value:** supply a new required secret or select an environment variable.
- **Enter an environment variable name:** a required environment-backed field cannot be blank.
- **Reserved name:** choose a name that does not shadow an integration slot.
- **Already exists:** another credential already resolves to that key.
- **Credential not found:** refresh the page. Another session may have removed it.
- **Still linked:** remove the model, embedding or speech references named in the warning.
- **Set but unavailable:** confirm the referenced environment variable exists in the running host process.
- **Stored value unavailable after restart:** sign in as its owner and unlock the Vault Password.

## Related articles

- [Connected accounts](references/user-guide-connections-connected-accounts.md)
- [Model providers](references/user-guide-concepts-model-providers.md)
- [Voice](references/user-guide-getting-started-voice.md)
- [Telegram](references/user-guide-connections-telegram.md)
- [Discord](references/user-guide-connections-discord.md)
