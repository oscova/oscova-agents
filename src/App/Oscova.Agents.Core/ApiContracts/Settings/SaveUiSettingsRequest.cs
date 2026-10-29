using System.ComponentModel.DataAnnotations;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.ApiContracts.Settings;

/// <summary>Saves the user-scoped UI settings managed from the settings page.</summary>
public sealed class SaveUiSettingsRequest
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
    [Range(AssistantUiSettings.MinimumLowBatteryTaskThresholdPercentage, AssistantUiSettings.MaximumLowBatteryTaskThresholdPercentage)]
    public int LowBatteryTaskThresholdPercentage { get; set; } = AssistantUiSettings.DefaultLowBatteryTaskThresholdPercentage;

    /// <summary>Copies every current value so a caller can change one field without resetting the rest.</summary>
    public static SaveUiSettingsRequest From(UiSettingsSummary current) => new()
    {
        Theme = NormalizeTheme(current.Theme),
        TimeZoneId = current.TimeZoneId,
        HideUserNameBadge = current.HideUserNameBadge,
        IsGamificationEnabled = current.IsGamificationEnabled,
        IsChatTitleGenerationEnabled = current.IsChatTitleGenerationEnabled,
        AreNotificationsMuted = current.AreNotificationsMuted,
        ChatFollowUpMode = current.ChatFollowUpMode,
        ShowLogWarningNotifications = current.ShowLogWarningNotifications,
        ShowSystemErrorNotifications = current.ShowSystemErrorNotifications,
        CultureName = UiCultureCatalog.NormalizeOrDefault(current.CultureName),
        ChatResumeWindowMinutes = current.ChatResumeWindowMinutes,
        IsLowBatteryTaskProtectionEnabled = current.IsLowBatteryTaskProtectionEnabled,
        LowBatteryTaskThresholdPercentage = current.LowBatteryTaskThresholdPercentage
    };

    /// <summary>Copies every current value so a caller can change one field without resetting the rest.</summary>
    public static SaveUiSettingsRequest From(AssistantUiSettings current) => new()
    {
        Theme = NormalizeTheme(current.Theme),
        TimeZoneId = current.TimeZoneId,
        HideUserNameBadge = current.HideUserNameBadge,
        IsGamificationEnabled = current.IsGamificationEnabled,
        IsChatTitleGenerationEnabled = current.IsChatTitleGenerationEnabled,
        AreNotificationsMuted = current.AreNotificationsMuted,
        ChatFollowUpMode = current.ChatFollowUpMode,
        ShowLogWarningNotifications = current.ShowLogWarningNotifications,
        ShowSystemErrorNotifications = current.ShowSystemErrorNotifications,
        CultureName = UiCultureCatalog.NormalizeOrDefault(current.CultureName),
        ChatResumeWindowMinutes = current.ChatResumeWindowMinutes,
        IsLowBatteryTaskProtectionEnabled = current.IsLowBatteryTaskProtectionEnabled,
        LowBatteryTaskThresholdPercentage = current.LowBatteryTaskThresholdPercentage
    };

    /// <summary>Writes every saved value back onto the shared settings cache after a successful save.</summary>
    public void ApplyTo(AssistantUiSettings target)
    {
        target.Theme = Theme;
        target.TimeZoneId = TimeZoneId;
        target.HideUserNameBadge = HideUserNameBadge;
        target.IsGamificationEnabled = IsGamificationEnabled;
        target.IsChatTitleGenerationEnabled = IsChatTitleGenerationEnabled;
        target.AreNotificationsMuted = AreNotificationsMuted;
        target.ChatFollowUpMode = ChatFollowUpMode;
        target.ShowLogWarningNotifications = ShowLogWarningNotifications;
        target.ShowSystemErrorNotifications = ShowSystemErrorNotifications;
        target.CultureName = CultureName;
        target.ChatResumeWindowMinutes = ChatResumeWindowMinutes;
        target.IsLowBatteryTaskProtectionEnabled = IsLowBatteryTaskProtectionEnabled;
        target.LowBatteryTaskThresholdPercentage = LowBatteryTaskThresholdPercentage;
    }

    private static string NormalizeTheme(string theme) =>
        string.IsNullOrWhiteSpace(theme) ? AssistantUiSettings.DefaultTheme : theme.Trim();
}
