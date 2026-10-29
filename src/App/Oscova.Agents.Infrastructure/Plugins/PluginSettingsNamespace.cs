namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Owns the plugin settings namespace shared by bound operations and channel admission.</summary>
internal static class PluginSettingsNamespace
{
    public static bool Owns(string pluginId, string key) => string.Equals(key, pluginId, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(pluginId + ".", StringComparison.OrdinalIgnoreCase);
    public static string Resolve(string pluginId, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        { throw new ArgumentException("Settings key must contain only ASCII letters, digits or hyphens.", nameof(key)); }
        // Accepted case variants share one storage identity on every supported filesystem.
        return pluginId.ToLowerInvariant() + "." + key;
    }
}
