# Connected accounts

Connected Accounts links your Oscova Agents user to Google or Microsoft so approved tools can use those services with your permission. Each connection belongs to one Oscova user and is stored encrypted under that user's Vault Password.

> **Note:**
> OAuth is the sign-in flow where an external provider, such as Google or Microsoft, asks whether Oscova may access selected services for your account.

## Supported providers

The page lists active account providers in alphabetical order:

- **Google** for Gmail, Calendar and YouTube tools, supplied by the optional Google Workspace plugin.
- **Microsoft** for Outlook and Microsoft To Do tools, supplied by the optional Microsoft Graph plugin.

Each provider card shows **Available**, **Unavailable**, **Connected as** or **Not connected**. **Connect** is disabled when the provider is unavailable.



- Disabling or removing either account plugin removes that provider's card, operator fields and tools after restart. Enable or reinstall it through **Plugins** to restore the provider.
- Google and Microsoft remain independent. Installation does not connect an account or grant tools to an agent.
- Other active plugins can contribute account cards and app credential fields through the same screens.

## Configure the OAuth app as an administrator

Google and Microsoft require a shared OAuth app configuration before users can connect personal accounts. An administrator saves the provider client ID and client secret in the admin OAuth provider card.

1. Create the OAuth app at Google or Microsoft.
2. Register the Oscova app address followed by `/api/oauth/google/callback` or `/api/oauth/microsoft/callback` as the redirect URI. For example, an Oscova opened at `http://localhost:11417` registers `http://localhost:11417/api/oauth/google/callback` for Google. Register one redirect URI for each address users open Oscova from.
3. Save the client ID and client secret in the admin OAuth provider card. The first save requires both. Later saves may leave the secret blank to keep the stored secret.

Clearing the client ID removes that provider app configuration and disables the provider. Users cannot repair this from Connected Accounts.

Connected Accounts stores user tokens, not general API keys. Each token belongs to the signed-in Oscova user, is encrypted in that user's credential store and is hidden from the Credentials page as a managed entry. The shared OAuth client ID and secret belong to the administrator's system configuration and are protected by the operator who saved them. After a Host restart, either kind remains unavailable until its protection owner signs in and unlocks. Another account cannot silently adopt an existing shared secret.

## Connect an account

1. Open **Settings > Accounts & Access > Connected Accounts**.
2. Select **Connect** on an available provider.
3. Complete the provider sign-in and consent flow.
4. Return to Oscova and confirm the card shows the connected account.

- Google requests offline access so Oscova can refresh the token without asking you to sign in for every tool call. Google scopes currently include OpenID profile and email, Gmail read and send, Calendar read-only and YouTube read-only access.
- Microsoft requests OpenID profile and email, `offline_access` and Microsoft Graph scopes for mail read and send, Microsoft To Do read and write and basic user profile lookup.
- The callback must complete in the same signed-in Oscova session that started the flow. Oscova checks the one-time OAuth state value and verifies the initiating user and provider before it exchanges the authorization code.
- Oscova builds the callback from the address used to open the app. Use an app address that the same browser can return to after provider consent. A stale host name, reverse-proxy address or inaccessible callback URL can prevent completion.
- Connected-account tools call Google or Microsoft over the network. They are external egress and are blocked in an offline run. Oscova refreshes expiring access tokens when a refresh token is available.

## Disconnect an account

Select **Disconnect** to remove Oscova's saved token for that provider. The card returns to **Not connected**.

Disconnecting stops future tool calls from using the saved connection. It does not delete your external account. To revoke consent at the provider, use that provider's security or app-permissions dashboard.

## Troubleshooting

- **Google or Microsoft is missing:** enable or install its account plugin in **Plugins**, save activation settings and restart Oscova.
- **Existing Google or Microsoft tool selections stopped working after the plugin upgrade:** reselect those tools in the agent editor. Their qualified names now include the plugin identity.
- **Connect is disabled:** ask an administrator to configure the provider's OAuth app.
- **The browser returns an error:** retry from the same signed-in Oscova session that started the flow.
- **The provider reports a redirect URI mismatch:** ask an administrator to register the callback address for the app address you opened, as described in Configure the OAuth app as an administrator.
- **Provider not configured:** ask an administrator to save the provider client ID and client secret. An administrator sees a **Manage providers** link that opens the admin OAuth provider card.
- **Invalid state or missing code:** start the connection again from the Connected Accounts card.
- **The connected email is missing:** the provider did not return an email. The token can still be saved.
- **A tool still cannot access the service:** confirm the active agent is allowed to use that tool and that the provider granted the required scopes.
- **A previously connected tool asks you to reconnect:** the refresh token may be missing, expired or rejected. Disconnect and complete the connection flow again.
- **The connection is unavailable after Oscova restarted:** enter your Vault Password when prompted before reconnecting.
- **Disconnect fails:** retry after refreshing the settings page. Unsupported provider names are rejected.
- **A different user needs access:** that user must connect their own account. OAuth tokens are scoped to the signed-in Oscova user.

## Related articles

- [Credentials](references/user-guide-settings-and-admin-credentials.md)
- [Tool governance](references/user-guide-concepts-tool-governance.md)
- [MCP servers](references/user-guide-connections-mcp-servers.md)
