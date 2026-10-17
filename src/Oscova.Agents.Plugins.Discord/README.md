# Oscova Discord Channel

This plugin provides the Discord transport for Oscova Agents. The host controls configuration storage, UI, message admission, and agent execution.

The channel declares one nonsecret field, `authorized_user_id`, and the `discord-bot` credential with a `botToken` field. Tokens belong in the host credential store. Configuration uses the declared `oscova.discord` settings key. Oscova Assistant stores it as `settings/oscova.discord.yaml` beneath the current user's data directory.

Configure a Discord bot with the message content intent and save its token in Credentials. Save the authorized user's Discord ID, then start the channel. The configured user can send messages in direct messages or guild channels visible to the bot.

Saving restarts an already running connection. A stopped connection stays stopped. Connection errors never roll back successfully saved settings. Plugin enablement and removal take effect after restarting Oscova Assistant.

## Registration

```csharp
builder.AddCredential(DiscordChannel.Credential)
    .AddChannel(new DiscordChannel());
```

Registration opens no network connection. The host creates a connection when requested and disposes it after stopping. Inbound callbacks must return after message admission so Discord can receive approval replies while an agent turn is running.

Channel icon: Bootstrap Icons discord.svg from https://github.com/twbs/icons/tree/6945b7006285d444cc17ff2e22c7691719229526, MIT license in Icons/LICENSE.txt. The root fill is set to #000000 for image display.
