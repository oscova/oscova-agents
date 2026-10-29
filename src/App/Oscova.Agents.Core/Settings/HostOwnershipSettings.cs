using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Stores the Identity user that owns same-OS-user host CLI access.
/// </summary>
public sealed class HostOwnershipSettings : SettingsBase
{
    public const string Key = "host-ownership";
    private string? _hostOwnerUserId;

    public HostOwnershipSettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
        : base(repository, WellKnownUsers.SystemId, loggerFactory)
    {
    }

    public override string SettingsKey => Key;

    public string? HostOwnerUserId
    {
        get => _hostOwnerUserId;
        set => SetProperty(ref _hostOwnerUserId, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            HostOwnerUserId = HostOwnerUserId
        });
    }

    public override void FromYaml(string yaml)
    {
        var snapshot = Yaml.Deserialize<Snapshot>(yaml);
        HostOwnerUserId = snapshot?.HostOwnerUserId;
    }

    private sealed class Snapshot
    {
        public string? HostOwnerUserId { get; set; }
    }
}
