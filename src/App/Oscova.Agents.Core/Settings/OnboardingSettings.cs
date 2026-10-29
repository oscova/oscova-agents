using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;
using Oscova.Agents.Persistence;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Per-user onboarding wizard progress and whether the wizard has been completed.
/// </summary>
public sealed class OnboardingSettings : SettingsBase
{
    public const string Key = "onboarding";

    private bool _isCompleted;
    private DateTimeOffset? _completedAt;
    private int _completedStep;
    private string? _profilePictureSeedName;

    public OnboardingSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
        : base(repository, userId, loggerFactory) { }

    public override string SettingsKey => Key;

    public bool IsCompleted
    {
        get => _isCompleted;
        set => SetProperty(ref _isCompleted, value);
    }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set => SetProperty(ref _completedAt, value);
    }

    /// <summary>Last wizard step the user moved past, so a reload or restart resumes after it.</summary>
    public int CompletedStep
    {
        get => _completedStep;
        set => SetProperty(ref _completedStep, value);
    }

    /// <summary>Built-in picture the user chose in the wizard, so going back shows it selected.</summary>
    public string? ProfilePictureSeedName
    {
        get => _profilePictureSeedName;
        set => SetProperty(ref _profilePictureSeedName, value);
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            IsCompleted = IsCompleted,
            CompletedAt = CompletedAt.HasValue ? IsoTimestamp.FormatSeconds(CompletedAt.Value) : null,
            CompletedStep = CompletedStep,
            ProfilePictureSeedName = ProfilePictureSeedName
        });
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is null)
        {
            return;
        }

        IsCompleted = data.IsCompleted;
        CompletedAt = string.IsNullOrWhiteSpace(data.CompletedAt) ? null : IsoTimestamp.Parse(data.CompletedAt);
        CompletedStep = data.CompletedStep;
        ProfilePictureSeedName = data.ProfilePictureSeedName;
    }

    private sealed class Snapshot
    {
        public bool IsCompleted { get; set; }
        public string? CompletedAt { get; set; }
        public int CompletedStep { get; set; }
        public string? ProfilePictureSeedName { get; set; }
    }
}
