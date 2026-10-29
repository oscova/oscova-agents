namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>
/// Captures the plugin allowlist applied when the current host process started.
/// </summary>
public sealed class PluginStartupState
{
    private HashSet<string> _enabledPluginIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets plugin identifiers that were permitted to load for this process.
    /// </summary>
    public IReadOnlySet<string> EnabledPluginIds => _enabledPluginIds;

    /// <summary>
    /// Records the persisted allowlist used by startup plugin loading.
    /// </summary>
    public void CaptureEnabledPluginIds(IEnumerable<string> pluginIds)
    {
        _enabledPluginIds = pluginIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a plugin was selected for loading when this process started.
    /// </summary>
    public bool WasEnabled(string pluginId)
    {
        return _enabledPluginIds.Contains(pluginId);
    }
}
