# Discord Bot

Answer Discord messages through the Discord plugin, installed by default. Its channel appears when the plugin is active.

> **In plain terms:** Only the authorized Discord user can use the bot.

## Configure access

- Store the token in **Discord Bot** credentials.
- Enter **Authorized User ID** and enable Message Content Intent.
- Choose the **Agent** that replies through Discord.
- Grant view and reply permissions in each server channel.
- Click **Save Settings** after changes.

> **Note:** The authorized user can use DMs and accessible server channels. Others are ignored.

Choose an agent you are authorized to use. Unlock your Vault Password in Oscova before it can process messages.

## Start, stop, and use the channel

- **Connect at startup** takes effect after you click **Save Settings**.
- Save restarts a running bot. A stopped bot stays stopped.
- Save changes before **Start Bot** becomes available.
- A failed save leaves the connection unchanged. A failed restart keeps your saved settings, so fix the connection problem and start again.
- Use **Start Bot** to connect or **Stop Bot** to disconnect.
- Startup needs credentials and network access.
- Chat and task notifications need a saved authorized user, a running bot, and permission to send DMs.
- Notifications temporarily route replies to the notified conversation's agent and project for your chat resume window.
- Send `/new` to continue with the selected agent in a fresh conversation.
- If an approval or question is waiting, `/new` cancels that run and its pending prompt first.
- Missing or unavailable selections use the main agent.
- Discord stays networked even when the agent is local-only.
- Messages accept 50 supported attachments, up to 10 MB each.
- If the default model cannot initialize, image support follows the fallback provider.
- Deleting the conversation also cancels its pending attachment transfers.
- Hooks inspect attachment metadata before download, and denials download nothing.
- Local-only skips attachments, while replies are audited as egress.
- Replies over 2,000 characters are split into multiple messages.

## Related

- [Telegram Bot](?helptopic=plugin%3Aoscova.telegram%3Asettings) - configure Telegram.
- [Credentials](?helptopic=settings.credentials) - save the bot token.

Uninstalling this plugin removes its stored credentials and connected-account tokens for all users.
