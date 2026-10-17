# Telegram Bot

Answer private Telegram messages through the default-installed Telegram plugin.

> **In plain terms:** Only one saved Telegram user can chat with this bot.

## Configure access

- Store the BotFather token in **Telegram Bot** credentials.
- Enter a numeric **Authorized Telegram User ID** from 1 to 9223372036854775807, not a username.
- Choose the **Agent** that replies through Telegram.
- Click **Save Settings** after editing the user ID, agent, or **Connect at startup**.
- Message the bot privately before expecting replies.

> **Note:** Group messages, other users, unsupported files, and files over 10 MB are ignored.

Choose an agent you are authorized to use. Unlock your Vault Password in Oscova before it can process messages.

## Start, stop, and use the channel

- Saving restarts a running bot and leaves a stopped bot stopped.
- If reconnection fails, saved settings remain intact. Fix the error, then start again.
- Save pending changes before **Start Bot** becomes available.
- Disabling the plugin removes its panel after restart and preserves its settings.
- Telegram supports chat only, with no task notifications.
- Use **Start Bot** to connect or **Stop Bot** to disconnect.
- Startup needs credentials and network access.
- After a temporary polling error, the next Telegram update restores the connected status.
- Messages use the selected agent's current conversation, tools, privacy, and approvals.
- Missing or unavailable selections use the main agent.
- Telegram stays networked even when the agent is local-only.
- Messages accept 50 supported attachments.
- If the default model cannot initialize, image support follows the fallback provider.
- Deleting the conversation also cancels its pending attachment transfers.
- Hooks inspect attachment metadata before download, and denials download nothing.
- Local-only skips attachments, while replies are audited as egress.
- Replies over 4,096 characters are split into multiple messages.

## Related

- [Discord Bot](?helptopic=plugin%3Aoscova.discord%3Asettings) - configure Discord.
- [Credentials](?helptopic=settings.credentials) - save the Telegram bot token.

Uninstalling this plugin removes its stored credentials and connected-account tokens for all users.
