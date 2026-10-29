using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>Retains public release identities already announced by this Host across restarts.</summary>
public sealed class UpdateNotificationSettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
    : SettingsBase(repository, WellKnownUsers.SystemId, loggerFactory)
{
    public const string Key = "update-notifications";
    private string? _harnessIdentity;
    private string? _avatarIdentity;
    public override string SettingsKey => Key;
    public string? HarnessIdentity { get => _harnessIdentity; set => SetProperty(ref _harnessIdentity, value); }
    public string? AvatarIdentity { get => _avatarIdentity; set => SetProperty(ref _avatarIdentity, value); }
    /// <summary>Serializes only the public release identities already announced.</summary>
    public override string ToYaml() => Yaml.Serialize(new Snapshot { HarnessIdentity = HarnessIdentity, AvatarIdentity = AvatarIdentity });
    /// <summary>Loads the last durable component identities for repeat suppression.</summary>
    public override void FromYaml(string yaml)
    {
        var snapshot = Yaml.Deserialize<Snapshot>(yaml);
        HarnessIdentity = snapshot?.HarnessIdentity;
        AvatarIdentity = snapshot?.AvatarIdentity;
    }
    private sealed class Snapshot
    {
        public string? HarnessIdentity { get; set; }
        public string? AvatarIdentity { get; set; }
    }
}
