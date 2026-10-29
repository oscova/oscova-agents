using Oscova.Agents.Core.Hardware;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.ApiContracts.Settings;

/// <summary>Describes the current user-scoped UI settings and related counts.</summary>
public sealed class UiSettingsSummary
{
    public string Theme { get; set; } = AssistantUiSettings.DefaultTheme;
    public string TimeZoneId { get; set; } = string.Empty;
    public bool HideUserNameBadge { get; set; }
    public bool IsGamificationEnabled { get; set; }
    public bool IsChatTitleGenerationEnabled { get; set; }
    public bool AreNotificationsMuted { get; set; }
    public ChatFollowUpMode ChatFollowUpMode { get; set; } = ChatFollowUpMode.Queue;
    public bool ShowLogWarningNotifications { get; set; } = true;
    public bool ShowSystemErrorNotifications { get; set; } = true;
    public string CultureName { get; set; } = UiCultureCatalog.DefaultCultureName;
    public int ChatResumeWindowMinutes { get; set; } = AssistantUiSettings.DefaultChatResumeWindowMinutes;
    public bool IsLowBatteryTaskProtectionEnabled { get; set; }
    public int LowBatteryTaskThresholdPercentage { get; set; } = AssistantUiSettings.DefaultLowBatteryTaskThresholdPercentage;
    // Host facts are returned for settings availability and are never saved in UI settings.
    public HostPowerSnapshot? HostPower { get; set; }
    public int ConversationCount { get; set; }
}
