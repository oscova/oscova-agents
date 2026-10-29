using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Per-user privacy posture and browser sign-out settings.
/// </summary>
public sealed class PrivacySettings : SettingsBase
{
    public const string Key = "privacy";

    private bool _isLocalOnly;
    private int _idleLogoutMinutes;

    public static IReadOnlyList<int> AllowedIdleLogoutMinutes { get; } = [0, 5, 15, 30];

    public PrivacySettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
        : base(repository, userId, loggerFactory)
    {
    }

    public override string SettingsKey => Key;

    /// <summary>
    /// When true, the user's agent may reach only loopback and local-network endpoints.
    /// </summary>
    public bool IsLocalOnly
    {
        get => _isLocalOnly;
        set => SetProperty(ref _isLocalOnly, value);
    }

    /// <summary>
    /// Gets or sets the human-idle period before browser sign-out; zero disables it.
    /// </summary>
    public int IdleLogoutMinutes
    {
        get => _idleLogoutMinutes;
        set => SetProperty(
            ref _idleLogoutMinutes,
            AllowedIdleLogoutMinutes.Contains(value) ? value : 0);
    }

    public Snapshot CreateSnapshot() => new()
    {
        IsLocalOnly = IsLocalOnly,
        IdleLogoutMinutes = IdleLogoutMinutes
    };

    public void ApplyFrom(Snapshot data)
    {
        IsLocalOnly = data.IsLocalOnly;
        IdleLogoutMinutes = data.IdleLogoutMinutes;
    }

    public override string ToYaml() => Yaml.Serialize(CreateSnapshot());

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is not null)
        {
            ApplyFrom(data);
        }
    }

    public sealed class Snapshot
    {
        public bool IsLocalOnly { get; set; }
        public int IdleLogoutMinutes { get; set; }
    }
}
