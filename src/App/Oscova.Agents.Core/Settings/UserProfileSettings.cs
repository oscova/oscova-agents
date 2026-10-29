using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>Stores optional per-user profile personalization outside account identity.</summary>
public sealed class UserProfileSettings : SettingsBase
{
    public const string Key = "profile";

    private string _nickname = string.Empty;

    public UserProfileSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
        : base(repository, userId, loggerFactory)
    {
    }

    public override string SettingsKey => Key;

    /// <summary>Gets or sets the name agents may use to address the user.</summary>
    public string Nickname
    {
        get => _nickname;
        set => SetProperty(ref _nickname, value ?? string.Empty);
    }

    public override string ToYaml() => Yaml.Serialize(new Snapshot
    {
        Nickname = Nickname
    });

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        Nickname = data.Nickname ?? string.Empty;
    }

    private sealed class Snapshot
    {
        public string? Nickname { get; set; }
    }
}
