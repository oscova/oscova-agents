using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>Retains the explicitly published skill packages under the operator's existing vault protection.</summary>
public sealed class SharedSkillSettings(IAppSettingsRepository repository, string userId, ILoggerFactory logs)
    : SettingsBase(repository, userId, logs)
{
    public const string Key = "shared-skills";
    public override string SettingsKey => Key;
    public IReadOnlyDictionary<string, byte[]> Packages { get; private set; } =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string[]> Recipients { get; private set; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Publishes a detached catalog so readers never enumerate a collection being edited.</summary>
    public void Replace(IReadOnlyDictionary<string, byte[]> packages, IReadOnlyDictionary<string, string[]> recipients)
    {
        Packages = new Dictionary<string, byte[]>(packages, StringComparer.OrdinalIgnoreCase);
        Recipients = new Dictionary<string, string[]>(recipients, StringComparer.OrdinalIgnoreCase);
        OnChanged();
    }

    public override string ToYaml() => Yaml.Serialize(new Snapshot { Packages = Packages.ToDictionary(), Recipients = Recipients.ToDictionary() });

    public override void FromYaml(string yaml)
    {
        var value = Yaml.Deserialize<Snapshot>(yaml) ?? new Snapshot();
        Replace(value.Packages, value.Recipients);
    }

    /// <summary>Stores package ownership with its published bytes in the operator vault.</summary>
    private sealed class Snapshot
    {
        public Dictionary<string, byte[]> Packages { get; set; } = [];
        public Dictionary<string, string[]> Recipients { get; set; } = [];
    }
}
