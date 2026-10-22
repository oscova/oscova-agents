# Slack Bot

Answer direct Slack messages from one authorized member in one workspace. The Slack plugin is installed by default and supplies this panel when active.

> **In plain terms:** Oscova uses your Slack app's Socket Mode connection to receive private messages. Only the saved user and workspace are accepted.

## Configure access

- Create a Slack app with Socket Mode, App Home messages, and the `message.im` bot event.
- Save its **Bot User OAuth Token** and **App-Level Token** in the plugin-declared **Slack Bot** credential.
- Open Slack in a browser and copy only the `T`-prefixed workspace ID after `/client/`, stopping at the next slash.
- In your Slack profile, choose **More > Copy member ID**. Enter it as the **Authorized Slack member ID**.
- Choose the replying **Agent**, then select **Save Settings**.
- Select **Start Bot** to connect. Enable **Connect at startup** and select **Save Settings** if it should reconnect after Oscova starts.

> **Note:** Slack may require a workspace administrator to approve the app.

> **Visibility:** Members of this Slack workspace can see the installed app. Oscova responds only to the authorized member's direct messages. For an app visible only to you, install it in a workspace where you are the only member.

## Use the channel

- Messages, supported files, approvals, and follow-up questions use the selected agent's current conversation.
- Saving restarts a running connection and leaves a stopped connection stopped. A failed reconnect keeps the saved settings.
- Stop and start the bot after replacing a token.
- Slack remains connected to its external servers even for local-only agents. Local-only runs skip file downloads.
- Group and shared-channel messages are ignored.
- Task notifications arrive as a direct message to the authorized member while the bot runs.
- Disabling or removing the plugin removes this panel after restart and keeps its saved configuration.

## Related

- [Credentials](?helptopic=settings.credentials) - store both Slack tokens.
- [Privacy](?helptopic=settings.privacy) - understand connected channel traffic.

Uninstalling this plugin removes its stored credentials and connected-account tokens for all users.

Choose an agent you are authorized to use. Unlock your Vault Password in Oscova before it can process messages.
