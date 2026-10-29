using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Per-user preferences for the subtle UI sound cues: a master on/off, a single master
/// volume, and an on/off flag per cue. Cues with no stored flag default to enabled.
/// </summary>
public sealed class SoundSettings : SettingsBase
{
    public const string Key = "sound";

    private bool _masterEnabled = true;
    private double _volume = 0.35;
    private Dictionary<string, bool> _cueEnabled = new(StringComparer.Ordinal);

    public SoundSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
        : base(repository, userId, loggerFactory) { }

    public override string SettingsKey => Key;

    public bool MasterEnabled
    {
        get => _masterEnabled;
        set => SetProperty(ref _masterEnabled, value);
    }

    public double Volume
    {
        get => _volume;
        set => SetProperty(ref _volume, Math.Clamp(value, 0d, 1d));
    }

    /// <summary>Per-cue on/off flags keyed by cue id. A missing key means the cue is enabled.</summary>
    public IReadOnlyDictionary<string, bool> CueEnabled => _cueEnabled;

    /// <summary>Returns whether a cue should play; unknown cues default to enabled.</summary>
    public bool IsCueEnabled(string cueId) =>
        !_cueEnabled.TryGetValue(cueId, out var isEnabled) || isEnabled;

    /// <summary>Sets a cue's on/off flag and raises the change event when it actually changes.</summary>
    public void SetCueEnabled(string cueId, bool isEnabled)
    {
        if (_cueEnabled.TryGetValue(cueId, out var current) && current == isEnabled)
        {
            return;
        }

        _cueEnabled[cueId] = isEnabled;
        OnChanged();
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            MasterEnabled = MasterEnabled,
            Volume = Volume,
            CueEnabled = new Dictionary<string, bool>(_cueEnabled, StringComparer.Ordinal)
        });
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is null)
        {
            return;
        }

        MasterEnabled = data.MasterEnabled;
        Volume = data.Volume;
        _cueEnabled = data.CueEnabled is null
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : new Dictionary<string, bool>(data.CueEnabled, StringComparer.Ordinal);
    }

    private sealed class Snapshot
    {
        public bool MasterEnabled { get; set; } = true;
        public double Volume { get; set; } = 0.35;
        public Dictionary<string, bool>? CueEnabled { get; set; }
    }
}
