using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Stores system-wide plugin activation choices and intentional removals.
/// </summary>
public sealed class PluginActivationSettings : SettingsBase
{
    public const string Key = "plugins";

    private HashSet<string> _enabledPluginIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _removedPluginIds = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _compatibilityDisabledReasons = new(StringComparer.OrdinalIgnoreCase);

    public PluginActivationSettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
        : base(repository, WellKnownUsers.SystemId, loggerFactory)
    {
    }

    public override string SettingsKey => Key;

    /// <summary>
    /// Gets stable plugin identifiers explicitly allowed to execute on the next startup.
    /// </summary>
    public IReadOnlyCollection<string> EnabledPluginIds => _enabledPluginIds;

    /// <summary>
    /// Gets stable plugin identifiers intentionally removed by an administrator.
    /// </summary>
    public IReadOnlyCollection<string> RemovedPluginIds => _removedPluginIds;

    /// <summary>Gets admission failures for installed plugins whose files have been preserved.</summary>
    public IReadOnlyDictionary<string, string> CompatibilityDisabledReasons => _compatibilityDisabledReasons;

    /// <summary>Records the current framework admission failures without changing removal choices.</summary>
    public void SetCompatibilityDisabledReasons(IReadOnlyDictionary<string, string> reasons)
    {
        _compatibilityDisabledReasons = new Dictionary<string, string>(reasons, StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(CompatibilityDisabledReasons));
        OnChanged();
    }

    /// <summary>
    /// Determines whether a discovered plugin is explicitly enabled for the next startup.
    /// </summary>
    public bool IsEnabled(string pluginId)
    {
        return _enabledPluginIds.Contains(pluginId);
    }

    /// <summary>
    /// Determines whether an official default plugin was intentionally removed.
    /// </summary>
    public bool IsRemoved(string pluginId)
    {
        return _removedPluginIds.Contains(pluginId);
    }

    /// <summary>
    /// Records or clears an administrator's intentional removal choice.
    /// </summary>
    public void SetRemoved(string pluginId, bool removed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var changed = removed
            ? _removedPluginIds.Add(pluginId.Trim())
            : _removedPluginIds.Remove(pluginId.Trim());
        if (!changed)
        {
            return;
        }

        OnPropertyChanged(nameof(RemovedPluginIds));
        OnChanged();
    }

    /// <summary>
    /// Replaces the allowlist with normalized plugin identifiers selected by an administrator.
    /// </summary>
    public void SetEnabledPluginIds(IEnumerable<string?>? pluginIds)
    {
        var normalizedIds = pluginIds?
            .Where(pluginId => !string.IsNullOrWhiteSpace(pluginId))
            .Select(pluginId => pluginId!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_enabledPluginIds.SetEquals(normalizedIds))
        {
            return;
        }

        _enabledPluginIds = normalizedIds;
        OnPropertyChanged(nameof(EnabledPluginIds));
        OnChanged();
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            EnabledPluginIds = [.. _enabledPluginIds.Order(StringComparer.OrdinalIgnoreCase)],
            RemovedPluginIds = [.. _removedPluginIds.Order(StringComparer.OrdinalIgnoreCase)],
            CompatibilityDisabledReasons = _compatibilityDisabledReasons
        });
    }

    public override void FromYaml(string yaml)
    {
        var snapshot = Yaml.Deserialize<Snapshot>(yaml);
        SetEnabledPluginIds(snapshot?.EnabledPluginIds);
        SetCompatibilityDisabledReasons(snapshot?.CompatibilityDisabledReasons ?? new Dictionary<string, string>());

        var removedPluginIds = snapshot?.RemovedPluginIds?
            .Where(pluginId => !string.IsNullOrWhiteSpace(pluginId))
            .Select(pluginId => pluginId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_removedPluginIds.SetEquals(removedPluginIds))
        {
            _removedPluginIds = removedPluginIds;
            OnPropertyChanged(nameof(RemovedPluginIds));
            OnChanged();
        }
    }

    private sealed class Snapshot
    {
        public List<string> EnabledPluginIds { get; set; } = [];
        public List<string> RemovedPluginIds { get; set; } = [];
        public Dictionary<string, string> CompatibilityDisabledReasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
