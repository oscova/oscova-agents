# Telegram

The optional Telegram plugin lets a selected Oscova Agents agent answer private Telegram messages through a bot. An active plugin adds its fields and icon to **Settings > Channels**. Disabling or uninstalling it removes that panel after restart and preserves saved settings.

> **Note:**
> A Telegram bot is created with BotFather. Oscova uses the saved bot token to long-poll Telegram and accepts only private messages from the saved numeric user ID.

## What you need first

- Create the bot in Telegram with BotFather and save its token in the **Telegram Bot** credential slot. The Telegram settings page does not store the token itself, and stored tokens need your vault unlocked.
- Find the numeric Telegram user ID of the one private user allowed to chat with the bot. This is not an `@username`. Tools such as `@userinfobot` can show the number.
- Start a conversation with the bot in Telegram before expecting replies. Telegram bots cannot initiate the first private conversation with a user.

## Configure the page

1. Open **Settings > Channels > Telegram**.
2. Turn on **Connect at startup** only if you want automatic connection on app start.
3. Enter the numeric **Authorized Telegram User ID**, from 1 to 9223372036854775807. A blank ID can be saved while startup is off, but the bot cannot start without a valid ID.
4. Choose the **Agent** that should reply. The main agent is selected by default. Choose an agent you are authorized to use.
5. Select **Save Settings**. All changes require it, including the startup toggle.
6. Select **Start Bot** to connect immediately. Save pending changes first.



- Saving writes your settings first, then restarts the bot if it was running. A stopped bot stays stopped.
- If the file cannot be saved, the current connection is left alone. If reconnection fails after a successful save, your saved values remain intact. Correct the reported issue and select **Start Bot**.
- If saved settings cannot be read, the page still shows connection status and **Stop Bot** for a running connection. Correct the reported settings problem before saving or starting again.
- The plugin saves its configuration encrypted under your Vault Password in `app_data/users/{userId}/settings/oscova.telegram.channel.yaml.enc`. The bot token stays in **Credentials**.

To disconnect now, select **Stop Bot**. If the bot should remain stopped after the next host restart, also turn off **Connect at startup** and save. After a host restart, sign in and enter your Vault Password before expecting an enabled bot to connect or process messages.

## Runtime behavior

While the bot runs, Oscova listens for private Telegram messages from the authorized user. Group chats and messages from other users are ignored. Telegram supports private chat only.

An agent can message you on Telegram from a web chat or an agent task. Your reply continues the notified conversation with its agent and project for your chat resume window. The latest notification replaces that destination. Send `/new` to start your next message in a fresh conversation with the selected agent. If an approval or tool question is waiting, `/new` cancels that run and its prompt first. Other text still answers the prompt. See [Notifications and agent messages](references/user-guide-automation-agent-tasks.md) for expiry and restart behavior.

- Telegram runs use the selected agent and its current provider, tools, skills, memory, privacy and file access. Hooks and tool approvals still apply: if a hook denies a prompt or tool call, the bot replies with the denial message instead of continuing silently. Tool approval prompts can be answered through Telegram when the selected tool policy requires confirmation.
- When a tool or MCP server asks a follow-up question through text elicitation, the next matching reply from the same private chat is consumed as that answer instead of starting a new run.
- Agent replies longer than Telegram's 4,096-character message limit are split into multiple messages.
- If the saved agent is deleted or no longer shared with you, the next message uses your main agent. The stale selection cannot enter or continue the former agent's conversation.
- Runs are saved into the selected agent's current chat thread and recorded in metrics. A new session started from Telegram gets the thread title **Telegram**. Outbound replies appear in the run's Egress Audit, while inbound polling and attachment downloads stay outside that ledger.
- Telegram polling and replies always use Telegram's external network service. A local-only user or agent does not download message attachments, but it does not make the Telegram channel itself offline.

### Attachments

- Text, captions, documents and photos can become agent input when they pass the same attachment validation used by web chat.
- Limits match web chat: up to 50 usable attachments per message and 10 MB per attachment. Supported files include images, PDFs and common text formats such as Markdown, TXT, JSON, CSV, XML, YAML and logs.
- Before downloading an attachment, Oscova gives `UserPromptSubmit` hooks its Telegram-supplied file name, media type and size. A denied prompt downloads and stages nothing.
- Unsupported files and files reported over the limit are skipped before download. When Telegram omits the size, the download stops if the actual bytes exceed the limit, and the temporary file is removed.
- Deleting the conversation cancels its pending attachment transfers.

### Follow-up messages

Text sent while the agent is working joins that response at its next model call, without a separate acknowledgement. Messages with attachments wait for the current run to finish and then run as their own turn. The web Follow-up behavior setting does not change channel messages, and live steering also works when the Resume recent chats window is zero or has expired.

## Troubleshooting

- **The Telegram panel is missing:** enable the Telegram plugin in **Plugins** and restart Oscova.
- **Start Bot asks you to save the bot token in Credentials:** create or update the **Telegram Bot** credential.
- **Start Bot warns about the user ID:** save a positive numeric **Authorized Telegram User ID** first.
- **The bot ignores messages:** make sure you are messaging it privately from the saved user ID.
- **A reply answered a tool prompt instead of starting a new chat:** finish the pending approval or elicitation first, then send a new message.
- **No first reply arrives:** open the bot conversation in Telegram before using it from Oscova.
- **Startup does not connect:** confirm the startup toggle and user ID were saved, then restart Oscova Agents.
- **The status changes to an error:** check that the token is valid and the Oscova host can reach Telegram. After a temporary polling failure, the next Telegram update restores the connected status.

## Related articles

- [Credentials](references/user-guide-settings-and-admin-credentials.md)
- [Discord](references/user-guide-connections-discord.md)
- [Hooks](references/user-guide-settings-and-admin-hooks.md)
