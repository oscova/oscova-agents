# Oscova Slack Channel

Provides Slack private-chat transport through the Oscova.Agents channel API. Registration declares configuration without connecting.

- Install `Oscova.Agents.Plugins.Slack` and enable `oscova.slack`. Oscova installs this plugin by default.
- Configure `slack-bot` with both required secret fields: `botToken` (Bot User OAuth Token) and `appLevelToken` (App-Level Token).
- Set `workspace_id` and `authorized_user_id` to the Slack workspace and member IDs. These are case-sensitive IDs, not display names.
- Enable Socket Mode, App Home messages, the `message.im` bot event, bot scopes `im:history`, `chat:write`, `files:read`, and app scope `connections:write`.
- Only the authorized member's direct messages in the configured workspace are accepted. Replies preserve Slack threads. Task notifications arrive as a direct message to the authorized member.

The host renders the declared fields and chooses their storage directory. In Oscova, nonsecret settings use `oscova.slack.yaml` in the user's settings directory. Tokens stay in Credentials. Save explicitly after editing startup options. Saving restarts a running connection and leaves a stopped connection stopped. A failed reconnect preserves saved values.

The plugin reserves a Slack app ID across connections created by the same loaded registration. It does not coordinate separate processes. The host owns attachment admission, storage, approvals, and conversation policy. File downloads begin only when the host opens their streams, accept only HTTPS URLs on `files.slack.com`, and reject redirects. The connection owns the supplied transport and uses it for both Slack API requests and file downloads.

Channel icon: Bootstrap Icons slack.svg from https://github.com/twbs/icons/tree/6945b7006285d444cc17ff2e22c7691719229526, MIT license in Icons/LICENSE.txt. The root fill is set to #000000 for image display.
