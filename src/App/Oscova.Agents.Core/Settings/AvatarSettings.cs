using Microsoft.Extensions.Logging;
using Oscova.Agents.Avatar.Contracts;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Persists shared avatar bridge and rendering settings for the singleton avatar runtime.
/// </summary>
public class AvatarSettings : SettingsBase
{
    public const string Key = "avatar";

    public const string DefaultBridgeUrl = "http://127.0.0.1:11318";
    public const float DefaultSpeechVolume = 1.0f;
    public const float DefaultEmotionBlendSpeed = 1.0f;
    public const float DefaultEmotionCueBlendInSeconds = 0.5f;
    public const float DefaultEmotionCueRestoreAfterSeconds = 3.0f;
    public const int DefaultBackgroundColor = 134;
    public const int DefaultResolutionWidth = 1280;
    public const int DefaultResolutionHeight = 720;
    public const int DefaultGraphicsPresetLevel = 1;
    public const bool DefaultRayTracingEnabled = false;

    private string _bridgeUrl = DefaultBridgeUrl;
    private AvatarAccessMode _preferredAccessMode = AvatarAccessMode.Shared;
    private string? _selectedAvatar;
    private string? _selectedHairId;
    private string? _selectedTopClothingId;
    private string? _selectedEmotion;
    private float _emotionBlendSpeed = DefaultEmotionBlendSpeed;
    private float _emotionCueBlendInSeconds = DefaultEmotionCueBlendInSeconds;
    private float _emotionCueRestoreAfterSeconds = DefaultEmotionCueRestoreAfterSeconds;
    private int _backgroundColorRed = DefaultBackgroundColor;
    private int _backgroundColorGreen = DefaultBackgroundColor;
    private int _backgroundColorBlue = DefaultBackgroundColor;
    private int _resolutionWidth = DefaultResolutionWidth;
    private int _resolutionHeight = DefaultResolutionHeight;
    private bool _isFullscreen;
    private int _graphicsPresetLevel = DefaultGraphicsPresetLevel;
    private bool _isRayTracingEnabled = DefaultRayTracingEnabled;
    private AvatarPresentationSettingsRequest _presentation = CreateDefaultPresentation();
    private float _speechVolume = DefaultSpeechVolume;

    public AvatarSettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
        : base(repository, WellKnownUsers.SystemId, loggerFactory) { }

    public override string SettingsKey => Key;

    public string BridgeUrl
    {
        get => _bridgeUrl;
        set => SetProperty(ref _bridgeUrl, string.IsNullOrWhiteSpace(value) ? DefaultBridgeUrl : value);
    }

    public AvatarAccessMode PreferredAccessMode
    {
        get => _preferredAccessMode;
        set => SetProperty(ref _preferredAccessMode, value);
    }

    public string? SelectedAvatar
    {
        get => _selectedAvatar;
        set => SetProperty(ref _selectedAvatar, value);
    }

    // These IDs are saved with the selected avatar and restored by AvatarRuntimeSettingsService.
    public string? SelectedHairId
    {
        get => _selectedHairId;
        set => SetProperty(ref _selectedHairId, value);
    }

    public string? SelectedTopClothingId
    {
        get => _selectedTopClothingId;
        set => SetProperty(ref _selectedTopClothingId, value);
    }

    public string? SelectedEmotion
    {
        get => _selectedEmotion;
        set => SetProperty(ref _selectedEmotion, value);
    }

    public float EmotionBlendSpeed
    {
        get => _emotionBlendSpeed;
        set => SetProperty(ref _emotionBlendSpeed, value);
    }

    // Seconds a reply's emotion cue takes to reach its expression.
    public float EmotionCueBlendInSeconds
    {
        get => _emotionCueBlendInSeconds;
        set => SetProperty(ref _emotionCueBlendInSeconds, value);
    }

    // Seconds from a cue's start until the face blends back to the base emotion.
    public float EmotionCueRestoreAfterSeconds
    {
        get => _emotionCueRestoreAfterSeconds;
        set => SetProperty(ref _emotionCueRestoreAfterSeconds, value);
    }

    public int BackgroundColorRed
    {
        get => _backgroundColorRed;
        set => SetProperty(ref _backgroundColorRed, value);
    }

    public int BackgroundColorGreen
    {
        get => _backgroundColorGreen;
        set => SetProperty(ref _backgroundColorGreen, value);
    }

    public int BackgroundColorBlue
    {
        get => _backgroundColorBlue;
        set => SetProperty(ref _backgroundColorBlue, value);
    }

    public int ResolutionWidth
    {
        get => _resolutionWidth;
        set => SetProperty(ref _resolutionWidth, value);
    }

    public int ResolutionHeight
    {
        get => _resolutionHeight;
        set => SetProperty(ref _resolutionHeight, value);
    }

    public bool IsFullscreen
    {
        get => _isFullscreen;
        set => SetProperty(ref _isFullscreen, value);
    }

    public int GraphicsPresetLevel
    {
        get => _graphicsPresetLevel;
        set => SetProperty(ref _graphicsPresetLevel, value);
    }

    public bool IsRayTracingEnabled
    {
        get => _isRayTracingEnabled;
        set => SetProperty(ref _isRayTracingEnabled, value);
    }

    // Both presets travel together from the saved settings to AvatarRuntimeSettingsService.
    public AvatarPresentationSettingsRequest Presentation
    {
        get => _presentation;
        set => SetProperty(ref _presentation, value);
    }

    public float SpeechVolume
    {
        get => _speechVolume;
        set => SetProperty(ref _speechVolume, value);
    }

    /// <summary>Creates a detached data snapshot of current values (used by the avatar tab's draft pattern).</summary>
    public Snapshot CreateSnapshot()
    {
        return new Snapshot
        {
            BridgeUrl = BridgeUrl,
            PreferredAccessMode = PreferredAccessMode,
            SelectedAvatar = SelectedAvatar,
            SelectedHairId = SelectedHairId,
            SelectedTopClothingId = SelectedTopClothingId,
            SelectedEmotion = SelectedEmotion,
            EmotionBlendSpeed = EmotionBlendSpeed,
            EmotionCueBlendInSeconds = EmotionCueBlendInSeconds,
            EmotionCueRestoreAfterSeconds = EmotionCueRestoreAfterSeconds,
            BackgroundColorRed = BackgroundColorRed,
            BackgroundColorGreen = BackgroundColorGreen,
            BackgroundColorBlue = BackgroundColorBlue,
            ResolutionWidth = ResolutionWidth,
            ResolutionHeight = ResolutionHeight,
            IsFullscreen = IsFullscreen,
            GraphicsPresetLevel = GraphicsPresetLevel,
            IsRayTracingEnabled = IsRayTracingEnabled,
            Presentation = Presentation.Clone(),
            SpeechVolume = SpeechVolume,
        };
    }

    /// <summary>Applies values from a snapshot onto this instance.</summary>
    public void ApplyFrom(Snapshot data)
    {
        BridgeUrl = data.BridgeUrl;
        PreferredAccessMode = data.PreferredAccessMode;
        SelectedAvatar = data.SelectedAvatar;
        SelectedHairId = data.SelectedHairId;
        SelectedTopClothingId = data.SelectedTopClothingId;
        SelectedEmotion = data.SelectedEmotion;
        EmotionBlendSpeed = data.EmotionBlendSpeed;
        EmotionCueBlendInSeconds = data.EmotionCueBlendInSeconds;
        EmotionCueRestoreAfterSeconds = data.EmotionCueRestoreAfterSeconds;
        BackgroundColorRed = data.BackgroundColorRed;
        BackgroundColorGreen = data.BackgroundColorGreen;
        BackgroundColorBlue = data.BackgroundColorBlue;
        ResolutionWidth = data.ResolutionWidth;
        ResolutionHeight = data.ResolutionHeight;
        IsFullscreen = data.IsFullscreen;
        GraphicsPresetLevel = data.GraphicsPresetLevel;
        IsRayTracingEnabled = data.IsRayTracingEnabled;
        Presentation = data.Presentation.Clone();
        SpeechVolume = data.SpeechVolume;
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(CreateSnapshot());
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is not null)
        {
            ApplyFrom(data);
        }
    }

    // Each draft owns its layout objects so edits cannot change another draft's defaults.
    private static AvatarPresentationSettingsRequest CreateDefaultPresentation() => new()
    {
        Side = new()
        {
            Yaw = -23f / 9f,
            PositionX = 0.065f,
            PositionY = 0.25f,
            Width = 0.415f,
            Height = 0.45f
        }
    };

    /// <summary>Plain data object for serialization and the draft/apply UI pattern.</summary>
    public sealed class Snapshot
    {
        public string BridgeUrl { get; set; } = DefaultBridgeUrl;
        public AvatarAccessMode PreferredAccessMode { get; set; } = AvatarAccessMode.Shared;
        public string? SelectedAvatar { get; set; }
        public string? SelectedHairId { get; set; }
        public string? SelectedTopClothingId { get; set; }
        public string? SelectedEmotion { get; set; }
        public float EmotionBlendSpeed { get; set; } = DefaultEmotionBlendSpeed;
        public float EmotionCueBlendInSeconds { get; set; } = DefaultEmotionCueBlendInSeconds;
        public float EmotionCueRestoreAfterSeconds { get; set; } = DefaultEmotionCueRestoreAfterSeconds;
        public int BackgroundColorRed { get; set; } = DefaultBackgroundColor;
        public int BackgroundColorGreen { get; set; } = DefaultBackgroundColor;
        public int BackgroundColorBlue { get; set; } = DefaultBackgroundColor;
        public int ResolutionWidth { get; set; } = DefaultResolutionWidth;
        public int ResolutionHeight { get; set; } = DefaultResolutionHeight;
        public bool IsFullscreen { get; set; }
        public int GraphicsPresetLevel { get; set; } = DefaultGraphicsPresetLevel;
        public bool IsRayTracingEnabled { get; set; } = DefaultRayTracingEnabled;
        public AvatarPresentationSettingsRequest Presentation { get; set; } = CreateDefaultPresentation();
        public float SpeechVolume { get; set; } = DefaultSpeechVolume;

        public Snapshot Clone()
        {
            return new Snapshot
            {
                BridgeUrl = BridgeUrl,
                PreferredAccessMode = PreferredAccessMode,
                SelectedAvatar = SelectedAvatar,
                SelectedHairId = SelectedHairId,
                SelectedTopClothingId = SelectedTopClothingId,
                SelectedEmotion = SelectedEmotion,
                EmotionBlendSpeed = EmotionBlendSpeed,
                EmotionCueBlendInSeconds = EmotionCueBlendInSeconds,
                EmotionCueRestoreAfterSeconds = EmotionCueRestoreAfterSeconds,
                BackgroundColorRed = BackgroundColorRed,
                BackgroundColorGreen = BackgroundColorGreen,
                BackgroundColorBlue = BackgroundColorBlue,
                ResolutionWidth = ResolutionWidth,
                ResolutionHeight = ResolutionHeight,
                IsFullscreen = IsFullscreen,
                GraphicsPresetLevel = GraphicsPresetLevel,
                IsRayTracingEnabled = IsRayTracingEnabled,
                Presentation = Presentation.Clone(),
                SpeechVolume = SpeechVolume,
            };
        }
    }
}
