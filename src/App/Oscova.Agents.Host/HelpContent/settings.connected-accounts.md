# Connected Accounts

Connect Google or Microsoft so approved agent tools can use those services with your permission.

> **In plain terms:** OAuth lets an external provider ask you to approve selected access.

## Connect a provider

- Click **Connect** beside an available provider.
- Google and Microsoft require their active, optionally installed account plugins.
- **Connect** is enabled only when the card shows **Available**.
- Finish consent for the same provider in the signed-in Oscova session that started it.
- The callback returns to the currently open app address.
- A connected card shows the email when the provider returns one.

> **Note:** **Unavailable** means the operator has not configured that provider's OAuth app. The operator sees **Manage providers** on the card to add it.

- The operator registers `<app address>/api/oauth/google/callback` or `<app address>/api/oauth/microsoft/callback` as the OAuth app's redirect URI.

- Each connection belongs to the signed-in user.
- Tokens are encrypted, managed automatically, and hidden from the Credentials page.
- Tokens use the connecting user's Vault Password protection. After a restart, they remain unavailable until that user unlocks.
- Account tools use external services and are blocked in air-gapped runs.
- Reselect older Microsoft tools in custom agents after the plugin becomes active.
- Oscova refreshes expiring access when the provider supplied a usable refresh token.

> **Note:** stored shared provider secrets use the operator's Vault Password protection. The operator must unlock them after a restart.

## Disconnect

- Click **Disconnect** to remove Oscova's saved provider token.
- Provider consent is not revoked, so use the provider's security dashboard if needed.
- Reconnect if tools report that an expired or rejected token cannot refresh.

## Related

- [Credentials](settings.credentials.md) - store copied API keys and bot tokens.
