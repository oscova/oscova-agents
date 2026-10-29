namespace Oscova.Agents.Core.Plugins.Registry;

/// <summary>
/// Installs, stages an update for, or removes a vetted NuGet plugin package.
/// </summary>
public interface IPluginInstallService
{
    /// <summary>
    /// Downloads the supplied version, or latest stable, and can require a reviewed manifest identity.
    /// </summary>
    Task<string> InstallAsync(
        string packageId,
        string? packageVersion,
        string? expectedPluginId,
        CancellationToken cancellationToken,
        string? replacePluginId = null,
        string? replacePackageId = null,
        bool explicitSelection = true);

    /// <summary>
    /// Removes an installed plugin package and any staged replacement.
    /// </summary>
    void Uninstall(string pluginId);
}

/// <summary>
/// Raised when another install or update has already claimed the plugin destination.
/// </summary>
public sealed class PluginInstallConflictException : Exception
{
    public PluginInstallConflictException(string message) : base(message)
    {
    }
}

/// <summary>Identifies the installed package owner that must be explicitly replaced.</summary>
public sealed class PluginPackageReplacementRequiredException(string pluginId, string? packageId)
    : Exception($"Plugin '{pluginId}' belongs to '{packageId ?? "a manual installation"}'. Explicit replacement is required.")
{
    public string PluginId { get; } = pluginId;
    public string? PackageId { get; } = packageId;
}

/// <summary>Raised before dependency restore when the plugin excludes the current runtime.</summary>
public sealed class PluginPlatformNotSupportedException(string pluginId, string runtimeIdentifier)
    : ArgumentException($"Plugin '{pluginId}' is not available on this platform ({runtimeIdentifier}).");
