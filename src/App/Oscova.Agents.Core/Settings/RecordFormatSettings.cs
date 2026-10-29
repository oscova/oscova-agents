using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Per-user format version of the plaintext record files, so their one-time conversion runs once.
/// </summary>
public sealed class RecordFormatSettings : SettingsBase
{
    public const string Key = "record-format";

    private int _version;

    public RecordFormatSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
        : base(repository, userId, loggerFactory)
    {
    }

    public override string SettingsKey => Key;

    /// <summary>
    /// Gets or sets the record format version; 1 means records hold ids instead of display names,
    /// 2 grants the main agent the shared folder, and 3 adds encrypted model-provider options.
    /// </summary>
    public int Version
    {
        get => _version;
        set => SetProperty(ref _version, value);
    }

    public override string ToYaml() => Yaml.Serialize(new Snapshot { Version = Version });

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is not null)
        {
            Version = data.Version;
        }
    }

    public sealed class Snapshot
    {
        public int Version { get; set; }
    }
}
