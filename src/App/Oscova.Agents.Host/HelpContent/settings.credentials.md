# Credentials

Credential lists adapt to the available width, with labelled groups on narrow screens.

Use Credentials to save API keys and tokens, then select them in provider or integration settings.

> **In plain terms:** store a secret here instead of pasting it into every feature.

## Add or edit

- Click **Add Credential** to create a free-form credential.
- Click **Edit** to fill a built-in integration slot.


- Stored values require your vault to be unlocked. Oscova asks for your Vault Password after a restart.
- Environment-variable entries remain available while locked because Oscova stores only the variable name.
- Scheduled tasks wait while the vault is locked. Unlock your Vault Password before running them. Stored credentials remain unavailable until their owner unlocks.

- **Set** confirms a stored value or variable-name reference exists.
- **Set** does not confirm the named environment variable has a runtime value.
- Integration credential names are fixed and their rows cannot be deleted.
- Connected Accounts tokens stay hidden from this page.

## Rename or delete

- Renaming updates user-scoped model, selected embedding, and speech references.
- Click **Delete** to remove only a free-form credential.
- Remove listed model, embedding, or speech references before deleting.
- Hook secrets do not block user credential deletion.

## Related

- [Connected Accounts](settings.connected-accounts.md) - manage Google and Microsoft tokens.
- [Privacy](settings.privacy.md) - review redaction and offline limits.
- [Save credential](dialog.credential-editor.md) - help for this dialog.
