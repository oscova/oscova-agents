using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

public sealed class AuthSettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
    : SettingsBase(repository, WellKnownUsers.SystemId, loggerFactory)
{
    public const string Key = "auth";

    private AuthMode _mode = AuthMode.Standard;
    private bool _requirePinForHome = true;

    public override string SettingsKey => Key;

    public AuthMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    public bool RequirePinForHome
    {
        get => _requirePinForHome;
        set => SetProperty(ref _requirePinForHome, value);
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            Mode = Mode,
            RequirePinForHome = RequirePinForHome
        });
    }

    public override void FromYaml(string yaml)
    {
        var snapshot = Yaml.Deserialize<Snapshot>(yaml);
        if (snapshot is null)
        {
            return;
        }

        Mode = snapshot.Mode;
        RequirePinForHome = snapshot.RequirePinForHome;
    }

    private sealed class Snapshot
    {
        public AuthMode Mode { get; set; } = AuthMode.Standard;

        public bool RequirePinForHome { get; set; } = true;
    }
}

public enum AuthMode
{
    Standard,
    Home
}
