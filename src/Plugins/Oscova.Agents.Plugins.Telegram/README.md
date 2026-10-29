# Oscova Telegram Channel

Provides Telegram private-chat transport through the Oscova.Agents channel API. Registration declares the channel and its credential without connecting.

- Install `Oscova.Agents.Plugins.Telegram` and enable `oscova.telegram`. Oscova installs this plugin by default.
- Configure `telegram-bot` with the secret `botToken` field. Create the bot through [BotFather](https://t.me/BotFather).
- Set `authorized_user_id` to a numeric Telegram user ID from 1 through 9223372036854775807, not a username. Start a conversation with the bot in Telegram first.
- The channel accepts private messages from that sender, including text, captions, documents, and photos. Task notifications go to that sender's private chat with the bot.

The host renders the declared settings fields and chooses their storage directory. In Oscova, nonsecret settings use `oscova.telegram.yaml` in the user's settings directory. Tokens stay in Credentials. Save explicitly after editing startup options. Saving restarts a running connection and leaves a stopped connection stopped. If reconnection fails, saved settings remain intact.

The host owns attachment admission, storage, approvals, and conversation policy. The plugin acquires attachment streams only when requested by the host. Local-only policy remains host-owned.

Channel icon: Bootstrap Icons telegram.svg from https://github.com/twbs/icons/tree/6945b7006285d444cc17ff2e22c7691719229526, MIT license in Icons/LICENSE.txt. The root fill is set to #000000 for image display.
