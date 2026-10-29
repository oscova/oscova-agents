# Discord

The optional Discord plugin lets a selected Oscova Agents agent answer Discord messages through a bot account. Choose the plugin during interactive startup or install it through **Plugins**, then enable it and restart. Its page appears under **Settings > Channels > Discord** only while the plugin is active. Disabling or removing it takes effect after restart and keeps your saved configuration.

> **Note:**
> A Discord bot is a separate Discord application account. Oscova signs in as that bot and only accepts messages from the numeric **Authorized User ID** saved on this page.

## What you need first

- Save the bot token in the plugin-declared **Discord Bot** credential slot. The Discord settings page does not ask for the token directly, and stored tokens need your vault unlocked.
- Copy your Discord user ID. It is a long number such as `123456789012345678`, not your username, display name or email address. Use the help link on the page for Discord's steps.
- Enable the Discord application's **Message Content Intent**.
- For server-channel use, invite the bot to the server and give it permission to view the chosen channel, read messages and send replies. Direct-message use requires the user and bot to be able to open a DM channel.

## Configure the page

1. Open **Settings > Channels > Discord**.
2. Turn on **Connect at startup** only if the bot should connect when Oscova starts. This requires a valid authorized user ID.
3. Enter your numeric **Authorized User ID**.
4. Choose the **Agent** that should reply. The main agent is selected by default. Choose an agent you are authorized to use.
5. Select **Save Settings**. All edits require it, including the startup toggle.
6. Select **Start Bot** to connect immediately. The button becomes available once edits are saved.



- Saving restarts a running connection with the saved values. A stopped channel stays stopped.
- If writing settings fails, the current connection continues unchanged and the page shows an error. If settings save but restarting fails, the saved values remain. Check credentials and connectivity, then select **Start Bot** again.
- If saved settings cannot be read, the page still shows connection status and **Stop Bot** for a running connection. Correct the reported settings problem before saving or starting again.
- The plugin saves its configuration encrypted under your Vault Password in `app_data/users/{userId}/settings/oscova.discord.channel.yaml.enc`. The token remains in Credentials.

To disconnect now, select **Stop Bot**. If the bot should stay stopped after the next host restart, also turn off the startup toggle and save. After a host restart, sign in and enter your Vault Password before expecting an enabled bot to connect.

## Runtime behavior

While the bot runs, messages from the authorized user in any direct or server channel the bot can receive go to the selected agent. Messages from every other user and from the bot itself are ignored.

An agent can message you on Discord from a web chat or an agent task. Your reply continues the notified conversation with its agent and project for your chat resume window. The latest notification replaces that destination. Send `/new` to start your next message in a fresh conversation with the selected agent. If an approval or tool question is waiting, `/new` cancels that run and its prompt first. Other text still answers the prompt. See [Notifications and agent messages](references/user-guide-automation-agent-tasks.md) for expiry and restart behavior.

- Discord runs use the selected agent and its current provider, tools, skills, memory, privacy and file access. Hooks and tool policies still apply: a `UserPromptSubmit` hook can block a message before it reaches the agent, and an approval policy can require confirmation before a tool call runs.
- Tool approval prompts and follow-up questions from a tool or MCP server are answered in the same Discord channel. The next matching reply from the authorized user is consumed as the answer instead of starting a new run. Bot-authored messages cannot answer them.
- Agent replies longer than Discord's 2,000-character message limit are split into multiple messages.
- If the saved agent is deleted or no longer shared with you, the next message uses your main agent. The stale selection cannot enter or continue the former agent's conversation.
- Runs are saved into the selected agent's current chat thread and recorded in metrics. A new session started from Discord gets the thread title **Discord**. Outbound replies appear in the run's Egress Audit, while inbound gateway traffic and attachment downloads stay outside that ledger.

### Attachments

- Limits match web chat: up to 50 usable attachments per message and 10 MB per attachment. Supported files include images, PDFs and common text formats such as Markdown, TXT, JSON, CSV, XML, YAML and logs.
- Before downloading an attachment, Oscova gives `UserPromptSubmit` hooks its Discord-supplied file name, media type and size. A denied prompt downloads and stages nothing. Accepted attachments use the same attachment path as web chat.
- Unsupported file extensions, oversized files and attachment-only messages with no usable content are skipped.
- If the selected agent or user is local-only, Oscova does not download attachments. The Discord gateway and outbound replies still use an external network service.
- Deleting the conversation cancels its pending attachment transfers.

### Follow-up messages

Text sent while the agent is working joins that response at its next model call, without a separate acknowledgement. Messages with attachments wait for the current run to finish and then run as their own turn. The web Follow-up behavior setting does not change channel messages, and live steering also works when the Resume recent chats window is zero or has expired.

## Troubleshooting

- **A task reports a failed Discord notification:** confirm the saved authorized user, start the bot and check that the bot can send you DMs.
- **Start Bot asks you to save the bot token in Credentials:** create or update the plugin-declared **Discord Bot** credential.
- **Start Bot warns about the user ID:** save a numeric **Authorized User ID** first.
- **The bot connects but does not answer:** confirm the author ID, Message Content Intent and the bot's access to that direct or server channel.
- **A reply answered a tool prompt instead of starting a new chat:** finish the pending approval or elicitation first, then send a new message.
- **The wrong person can talk to the bot:** recheck the numeric authorized ID. The page does not use display names.
- **Startup does not connect:** confirm both the startup toggle and authorized ID were saved, then restart Oscova Agents.

## Related articles

- [Credentials](references/user-guide-settings-and-admin-credentials.md)
- [Telegram](references/user-guide-connections-telegram.md)
- [Hooks](references/user-guide-settings-and-admin-hooks.md)
