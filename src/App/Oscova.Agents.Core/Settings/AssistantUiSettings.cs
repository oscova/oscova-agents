using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Per-user interface and experience preferences.
/// </summary>
public class AssistantUiSettings : SettingsBase
{
    public const string Key = "ui";
    public static readonly string DefaultTheme = "Light";
    public static readonly string DefaultTimeZoneId = TimeZoneInfo.Local.Id;
    public const int DefaultChatResumeWindowMinutes = 10;
    public const int DefaultLowBatteryTaskThresholdPercentage = 10;
    public const int MinimumLowBatteryTaskThresholdPercentage = 1;
    public const int MaximumLowBatteryTaskThresholdPercentage = 100;

    private string _theme = DefaultTheme;
    private bool _hideUserNameBadge;
    private bool _isGamificationEnabled;
    private bool _isChatTitleGenerationEnabled;
    private bool _areNotificationsMuted;
    private ChatFollowUpMode _chatFollowUpMode = ChatFollowUpMode.Queue;
    private bool _showLogWarningNotifications = true;
    private bool _showSystemErrorNotifications = true;
    private string _timeZoneId = string.Empty;
    private string _cultureName = UiCultureCatalog.DefaultCultureName;
    private int _chatResumeWindowMinutes = DefaultChatResumeWindowMinutes;
    private bool _isLowBatteryTaskProtectionEnabled;
    private int _lowBatteryTaskThresholdPercentage = DefaultLowBatteryTaskThresholdPercentage;

    public AssistantUiSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
        : base(repository, userId, loggerFactory) { }

    public override string SettingsKey => Key;

    public string Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public string TimeZoneId
    {
        get => _timeZoneId;
        set => SetProperty(ref _timeZoneId, value);
    }

    public bool HideUserNameBadge
    {
        get => _hideUserNameBadge;
        set => SetProperty(ref _hideUserNameBadge, value);
    }

    public bool IsGamificationEnabled
    {
        get => _isGamificationEnabled;
        set => SetProperty(ref _isGamificationEnabled, value);
    }

    public bool IsChatTitleGenerationEnabled
    {
        get => _isChatTitleGenerationEnabled;
        set => SetProperty(ref _isChatTitleGenerationEnabled, value);
    }

    /// <summary>Allows automatic task starts to be skipped on a confirmed low, unplugged battery.</summary>
    public bool IsLowBatteryTaskProtectionEnabled
    {
        get => _isLowBatteryTaskProtectionEnabled;
        set => SetProperty(ref _isLowBatteryTaskProtectionEnabled, value);
    }

    /// <summary>Sets the charge percentage below which automatic task starts may be skipped.</summary>
    public int LowBatteryTaskThresholdPercentage
    {
        get => _lowBatteryTaskThresholdPercentage;
        set => SetProperty(ref _lowBatteryTaskThresholdPercentage, value);
    }

    public bool AreNotificationsMuted
    {
        get => _areNotificationsMuted;
        set => SetProperty(ref _areNotificationsMuted, value);
    }

    public ChatFollowUpMode ChatFollowUpMode
    {
        get => _chatFollowUpMode;
        set => SetProperty(ref _chatFollowUpMode, value);
    }

    public bool ShowLogWarningNotifications
    {
        get => _showLogWarningNotifications;
        set => SetProperty(ref _showLogWarningNotifications, value);
    }

    public bool ShowSystemErrorNotifications
    {
        get => _showSystemErrorNotifications;
        set => SetProperty(ref _showSystemErrorNotifications, value);
    }

    public string CultureName
    {
        get => _cultureName;
        set => SetProperty(ref _cultureName, UiCultureCatalog.NormalizeOrDefault(value));
    }

    public int ChatResumeWindowMinutes
    {
        get => _chatResumeWindowMinutes;
        set => SetProperty(ref _chatResumeWindowMinutes, NormalizeChatResumeWindowMinutes(value));
    }

    /// <summary>
    /// Keeps resume-window values in the supported range; zero means automatic resume is disabled.
    /// </summary>
    public static int NormalizeChatResumeWindowMinutes(int value)
    {
        return Math.Max(0, value);
    }

    public TimeZoneInfo GetTimeZone()
    {
        return TimeZoneResolver.ResolveOrLocal(_timeZoneId);
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            Theme = Theme,
            HideUserNameBadge = HideUserNameBadge,
            IsGamificationEnabled = IsGamificationEnabled,
            IsChatTitleGenerationEnabled = IsChatTitleGenerationEnabled,
            AreNotificationsMuted = AreNotificationsMuted,
            ChatFollowUpMode = ChatFollowUpMode,
            ShowLogWarningNotifications = ShowLogWarningNotifications,
            ShowSystemErrorNotifications = ShowSystemErrorNotifications,
            TimeZoneId = TimeZoneId,
            CultureName = CultureName,
            ChatResumeWindowMinutes = ChatResumeWindowMinutes,
            IsLowBatteryTaskProtectionEnabled = IsLowBatteryTaskProtectionEnabled,
            LowBatteryTaskThresholdPercentage = LowBatteryTaskThresholdPercentage
        });
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is null)
        {
            return;
        }

        Theme = string.IsNullOrWhiteSpace(data.Theme) ? DefaultTheme : data.Theme;
        HideUserNameBadge = data.HideUserNameBadge;
        IsGamificationEnabled = data.IsGamificationEnabled;
        IsChatTitleGenerationEnabled = data.IsChatTitleGenerationEnabled;
        AreNotificationsMuted = data.AreNotificationsMuted;
        ChatFollowUpMode = data.ChatFollowUpMode;
        ShowLogWarningNotifications = data.ShowLogWarningNotifications;
        ShowSystemErrorNotifications = data.ShowSystemErrorNotifications;
        TimeZoneId = data.TimeZoneId ?? string.Empty;
        CultureName = UiCultureCatalog.NormalizeOrDefault(data.CultureName);
        ChatResumeWindowMinutes = NormalizeChatResumeWindowMinutes(data.ChatResumeWindowMinutes);
        IsLowBatteryTaskProtectionEnabled = data.IsLowBatteryTaskProtectionEnabled;
        LowBatteryTaskThresholdPercentage = data.LowBatteryTaskThresholdPercentage;
    }

    private sealed class Snapshot
    {
        public string? Theme { get; set; }
        public bool HideUserNameBadge { get; set; }
        public bool IsGamificationEnabled { get; set; }
        public bool IsChatTitleGenerationEnabled { get; set; }
        public bool AreNotificationsMuted { get; set; }
        public ChatFollowUpMode ChatFollowUpMode { get; set; } = ChatFollowUpMode.Queue;
        public bool ShowLogWarningNotifications { get; set; } = true;
        public bool ShowSystemErrorNotifications { get; set; } = true;

        public string? TimeZoneId { get; set; }

        public string? CultureName { get; set; }
        public int ChatResumeWindowMinutes { get; set; } = DefaultChatResumeWindowMinutes;
        public bool IsLowBatteryTaskProtectionEnabled { get; set; }
        public int LowBatteryTaskThresholdPercentage { get; set; } = DefaultLowBatteryTaskThresholdPercentage;
    }
}
