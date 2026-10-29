# Slack

The optional Slack plugin lets one selected Oscova Agents agent answer direct messages from one authorized Slack user in one workspace. Oscova connects through Socket Mode, so the app does not need a public request URL. Slack documents this connection method.

Choose the plugin during interactive startup or install it through **Plugins**. Enable it and restart to make its page available under **Settings > Channels > Slack**.

## Create and install a Slack app

1. Create an app from the manifest below in Your apps, selecting the workspace where the authorized user will message it. The fields follow Slack's app manifest reference.
2. In **Basic Information > App-Level Tokens**, create an App-Level Token with `connections:write`.
3. Install the app to the workspace under **OAuth & Permissions**, then copy its **Bot User OAuth Token**. A workspace administrator may need to approve the installation.
4. Copy the workspace ID. Open the workspace in a browser and take only the ID starting with `T` after `/client/`, stopping at the next slash before the channel ID. Slack explains where to find the workspace ID.
5. Copy your member ID. Open your own Slack profile and choose **More > Copy member ID**. Slack documents member IDs. Both values are opaque IDs, not display names.
6. Save both tokens in the plugin-declared **Slack Bot** credential under **Settings > Accounts & Access > Credentials**. The Slack settings page never displays or stores token values.

```yaml
display_information:
  name: Oscova
features:
  app_home:
    messages_tab_enabled: true
    messages_tab_read_only_enabled: false
  bot_user:
    display_name: Oscova
  slash_commands:
    - command: /new
      description: Start a fresh Oscova conversation
      should_escape: false
oauth_config:
  scopes:
    bot:
      - im:history
      - chat:write
      - files:read
      - commands
settings:
  event_subscriptions:
    bot_events:
      - message.im
  socket_mode_enabled: true
```

An undistributed app stays in its associated workspace and is not listed for other workspaces. Members of that workspace can view installed apps. Oscova ignores messages from members other than the authorized one, but that does not hide the app from them. If the app must be visible only to you, install it in a workspace where you are the only member.

## Connect Oscova

For an existing Slack app, add the `/new` slash command and `commands` bot scope from the manifest, then reinstall the app to authorize the added scope. Socket Mode delivers slash commands without a public request URL. Use `/new` without arguments in your direct message with the app.

1. Open **Settings > Channels > Slack**.
2. Enter the **Slack workspace ID** and **Authorized Slack member ID**.
3. Choose the replying **Agent**. Choose an agent you are authorized to use.
4. Select **Save Settings**, then **Start Bot**.
5. Optionally enable **Connect at startup** and select **Save Settings** for future host starts.



- Start uses the saved identity and credential, so save identity or agent edits first. Saving restarts a running connection and leaves a stopped connection stopped. If saving fails, the current connection remains unchanged. If reconnection fails after saving, the new values remain saved.
- Replacing a token requires **Stop Bot** and **Start Bot**.
- If the same Slack app is already connected for another Oscova account on this host, the second start is refused. Do not run the same Slack app in another Oscova instance at the same time.
- The status shows the observed connection state. A temporary disconnection leaves **Stop Bot** available while the SDK reconnects.
- If saved settings cannot be read, the page still shows connection status and **Stop Bot** for a running connection. Correct the reported settings problem before saving or starting again.
- The plugin saves its configuration encrypted under your Vault Password in `app_data/users/{userId}/settings/oscova.slack.channel.yaml.enc`. Tokens stay in Credentials. Disabling or uninstalling the plugin removes its panel and runtime after restart while preserving saved configuration.

To disconnect now, select **Stop Bot**. If the app should stay stopped after a host restart, also turn off **Connect at startup** and save. Stored tokens require the Oscova vault to be unlocked when the bot starts, so unlock your Vault Password before expecting the bot to process messages.

An agent can message you privately on Slack from a web chat or an agent task. Your reply continues the notified conversation with its agent and project for your chat resume window. The latest notification replaces that destination. Send `/new` to start your next message in a fresh conversation with the selected agent. If an approval or tool question is waiting, `/new` cancels that run and its prompt first. Other text still answers the prompt. See [Notifications and agent messages](references/user-guide-automation-agent-tasks.md) for expiry and restart behavior.

## Messages and files

Oscova accepts direct messages from the saved user and workspace. Group, shared-channel, bot and other-user messages are ignored.

- Direct-message threads share the selected agent's current Oscova conversation. A reply to a pending approval or follow-up question is consumed as that answer.
- The selected agent's current provider, tools, memory and privacy settings apply. If that agent becomes unavailable, the next message uses the main agent.
- Replies are recorded in the run's Egress Audit. Long replies are sent in multiple Slack messages.
- Slack transport uses Slack's external servers for inbound messages, approvals and replies, even when the agent is local-only.

### Attachments

- Supported images, PDFs and common text files can enter the run after attachment validation. Limits are 50 files per message and 10 MB per file. Unsupported or oversized files are skipped.
- Prompt hooks run before download, and a rejected prompt downloads nothing.
- Local-only runs skip Slack file downloads.

### Follow-up messages

Text sent while the agent is working joins that response at its next model call, without a separate acknowledgement. Messages with attachments wait for the current run to finish and then run as their own turn. The web Follow-up behavior setting does not change channel messages, and live steering also works when the Resume recent chats window is zero or has expired.

## Troubleshooting

- **Start fails:** check both saved IDs, both tokens, Socket Mode, the bot scopes and app installation.
- **Unknown `/new` command:** add the slash command to the Slack app and reinstall it with the `commands` scope.
- **No reply:** message the app directly from the saved user in the saved workspace. Check that App Home messages and `message.im` are enabled.
- **Disconnected:** check network access and Slack status. The connection can reconnect without another Start.
- **A reply answers a prompt:** complete the pending approval or follow-up question before sending a new task.

## Related articles

- [Credentials](references/user-guide-settings-and-admin-credentials.md)
- [Privacy](references/user-guide-settings-and-admin-privacy.md)
- [Telegram](references/user-guide-connections-telegram.md)
