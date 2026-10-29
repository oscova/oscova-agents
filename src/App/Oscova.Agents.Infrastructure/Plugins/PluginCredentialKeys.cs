namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Owns the private storage namespace for plugin credentials.</summary>
public static class PluginCredentialKeys
{
    public static string Prefix(string pluginId) => $"plugin:{pluginId.Trim().ToLowerInvariant()}:";
    public static string Qualify(string pluginId, string localKey)
        => Prefix(pluginId) + localKey.Trim().ToLowerInvariant();

    /// <summary>Names the managed token payload for a plugin's connected account.</summary>
    public static string OAuthToken(string pluginId, string providerId) => Qualify(pluginId, $"oauth:{providerId}");

    /// <summary>Identifies managed OAuth payload keys for diagnostic token redaction.</summary>
    public static bool IsOAuthToken(string storedKey) => storedKey.StartsWith("plugin:", StringComparison.Ordinal)
        && storedKey.Contains(":oauth:", StringComparison.Ordinal);
}
