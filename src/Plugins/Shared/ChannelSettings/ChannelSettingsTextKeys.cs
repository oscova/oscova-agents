#if DISCORD_PLUGIN
namespace Oscova.Agents.Plugins.Discord.Settings;
#elif TELEGRAM_PLUGIN
namespace Oscova.Agents.Plugins.Telegram.Settings;
#elif SLACK_PLUGIN
namespace Oscova.Agents.Plugins.Slack.Settings;
#endif

/// <summary>Names the localized controls shared by the official channel components.</summary>
public static class ChannelSettingsTextKeys
{
    /// <summary>Names the EnableAtStartupLabel display resource.</summary>
    public const string EnableAtStartupLabel = "Settings.Channels.EnableAtStartupLabel";
    /// <summary>Names the EnableAtStartupHint display resource.</summary>
    public const string EnableAtStartupHint = "Settings.Channels.EnableAtStartupHint";
    /// <summary>Names the TokensManagedDescription display resource.</summary>
    public const string TokensManagedDescription = "Settings.Channels.TokensManagedDescription";
    /// <summary>Names the ManageCredentials display resource.</summary>
    public const string ManageCredentials = "Settings.Channels.NewCredentialButton";
    /// <summary>Names the AgentLabel display resource.</summary>
    public const string AgentLabel = "Settings.Section.Agent.Label";
    /// <summary>Names the AgentHint display resource.</summary>
    public const string AgentHint = "Settings.Channels.AgentHint";
    /// <summary>Names the SaveSettingsButton display resource.</summary>
    public const string SaveSettingsButton = "Settings.Channels.SaveSettingsButton";
    /// <summary>Names the StatusTitle display resource.</summary>
    public const string StatusTitle = "Settings.Channels.StatusTitle";
    /// <summary>Names the SaveBeforeStartHint display resource.</summary>
    public const string SaveBeforeStartHint = "Settings.Channels.SaveBeforeStartHint";
    /// <summary>Names the StopBotButton display resource.</summary>
    public const string StopBotButton = "Settings.Channels.StopBotButton";
    /// <summary>Names the StartBotButton display resource.</summary>
    public const string StartBotButton = "Settings.Channels.StartBotButton";
    /// <summary>Names the Saved display resource.</summary>
    public const string Saved = "Settings.Shared.SaveSettingsToast";
    /// <summary>Names the Connected display resource.</summary>
    public const string Connected = "Common.Connected";
    /// <summary>Names the Disconnected display resource.</summary>
    public const string Disconnected = "Common.Disconnected";
    /// <summary>Names the Loading display resource.</summary>
    public const string Loading = "Common.Loading";
    /// <summary>Names the Error display resource.</summary>
    public const string Error = "Dialog.LogLevelError";
}
