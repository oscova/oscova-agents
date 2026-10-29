namespace Oscova.Agents.Core.Plugins.Registry;

/// <summary>
/// A NuGet package discovered as an installable Oscova plugin.
/// </summary>
public sealed class RemotePluginCatalogEntry
{
    public const string RequiresNewerOscova = "requires-newer-oscova";
    public const string Incompatible = "incompatible";
    public const string PrereleaseOnly = "prerelease-only";

    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FrameworkVersionRange { get; set; } = string.Empty;
    public string Authors { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ProjectUrl { get; set; }
    public string? LicenseExpression { get; set; }
    public string? IconUrl { get; set; }
    public long? DownloadCount { get; set; }
    public bool IsVerified { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Why this listed package cannot be installed here, or null when it can.</summary>
    public string? UnavailableReason { get; set; }
}
