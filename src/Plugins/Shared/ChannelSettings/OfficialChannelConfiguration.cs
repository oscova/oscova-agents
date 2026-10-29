#if DISCORD_PLUGIN
namespace Oscova.Agents.Plugins.Discord.Settings;
#elif TELEGRAM_PLUGIN
namespace Oscova.Agents.Plugins.Telegram.Settings;
#elif SLACK_PLUGIN
namespace Oscova.Agents.Plugins.Slack.Settings;
#endif

/// <summary>Shares the official channel plugins' startup and agent preferences without constraining other plugins.</summary>
public abstract record OfficialChannelConfiguration
{
    /// <summary>Gets whether the user's channel connects after startup and vault unlock.</summary>
    public bool StartAtStartup { get; init; }
    /// <summary>Gets the replying agent, or null for the default agent.</summary>
    public string? AgentId { get; init; }
}
