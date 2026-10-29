namespace Oscova.Agents.Core.Plugins.Registry;

/// <summary>
/// Searches the NuGet-backed plugin catalog without installing downloaded code.
/// </summary>
public interface IPluginRegistryService
{
    /// <summary>
    /// Searches NuGet.org for OscovaPlugin packages matching <paramref name="query"/>. An empty query returns no entries.
    /// </summary>
    Task<IReadOnlyList<RemotePluginCatalogEntry>> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns catalog metadata for a single package identifier, or null when it cannot be found.
    /// </summary>
    Task<RemotePluginCatalogEntry?> GetMetadataAsync(string packageId, CancellationToken cancellationToken);

    /// <summary>Lists stable releases admitted by the supplied framework without downloading archives.</summary>
    Task<IReadOnlyList<RemotePluginCatalogEntry>> GetReleasesAsync(string packageId, string frameworkVersion, CancellationToken cancellationToken);
}

/// <summary>
/// Represents an upstream NuGet registry failure that should be surfaced without treating it as a host bug.
/// </summary>
public sealed class NuGetPluginRegistryException : Exception
{
    public NuGetPluginRegistryException(string message) : base(message)
    {
    }

    public NuGetPluginRegistryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
