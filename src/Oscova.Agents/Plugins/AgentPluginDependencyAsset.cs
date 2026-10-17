namespace Oscova.Agents.Plugins;

/// <summary>Declares dependency files needed by a plugin outside NuGet's standard runtime asset groups.</summary>
public sealed class AgentPluginDependencyAsset
{
    /// <summary>Gets the package identifier already present in the plugin's restored dependency graph.</summary>
    public string PackageId { get; init; } = string.Empty;

    /// <summary>Gets an exact package-relative file or a directory ending in a forward slash.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the required runtime identifier, or null when the files apply on every supported host.</summary>
    public string? Rid { get; init; }
}
