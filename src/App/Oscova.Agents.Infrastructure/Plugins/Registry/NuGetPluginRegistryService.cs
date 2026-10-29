using System.Text.Json;
using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Oscova.Agents.Core.ApiContracts.Updates;
using Oscova.Agents.Plugins;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Infrastructure.Updates;

namespace Oscova.Agents.Infrastructure.Plugins.Registry;

/// <summary>Searches NuGet.org for OscovaPlugin packages with HTTP package-type filtering and reads per-package metadata.</summary>
public sealed class NuGetPluginRegistryService(
    IHttpClientFactory httpClientFactory,
    ILogger<NuGetPluginRegistryService> logger) : IPluginRegistryService
{
    private const string PluginPackageType = "OscovaPlugin";
    private const string SearchHttpClientName = "nuget-search";
    private const int SearchTake = 50;

    public async Task<IReadOnlyList<RemotePluginCatalogEntry>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // Prerelease-only plugins are listed too, so the dialog can say why they cannot be installed.
        var requestUri = $"query?q={Uri.EscapeDataString(query.Trim())}&packageType={Uri.EscapeDataString(PluginPackageType)}&prerelease=true&take={SearchTake}";

        try
        {
            var client = httpClientFactory.CreateClient(SearchHttpClientName);
            using var response = await client.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                var matches = ParseSearchResponse(document);
                var releases = await Task.WhenAll(matches.Select(entry => GetMetadataAsync(entry.Id, cancellationToken))).ConfigureAwait(false);
                for (var index = 0; index < releases.Length; index++)
                {
                    if (releases[index] is { } release) { release.IsVerified = matches[index].IsVerified; }
                }

                return releases.OfType<RemotePluginCatalogEntry>().ToArray();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "NuGet plugin search failed for query {Query}.", query);
            throw new NuGetPluginRegistryException("NuGet.org could not be searched for plugins.", ex);
        }
    }

    public async Task<RemotePluginCatalogEntry?> GetMetadataAsync(string packageId, CancellationToken cancellationToken)
    {
        var current = PluginFrameworkCompatibility.CurrentVersion;
        var releases = await GetReleasesFromSourceAsync(packageId, includePrerelease: false, cancellationToken).ConfigureAwait(false);
        if (releases.Count == 0)
        {
            // Show the newest prerelease so the plugin can be listed with the reason it cannot be installed.
            var prerelease = (await GetReleasesFromSourceAsync(packageId, includePrerelease: true, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            if (prerelease is not null) { prerelease.UnavailableReason = RemotePluginCatalogEntry.PrereleaseOnly; }

            return prerelease;
        }

        var compatible = releases.FirstOrDefault(release => PluginFrameworkCompatibility.GetIncompatibility(release.FrameworkVersionRange, current) is null);
        if (compatible is not null) { return compatible; }

        // No stable release fits this framework, so list the newest one as unavailable instead of hiding the plugin.
        var latest = releases[0];
        latest.UnavailableReason = VersionRange.TryParse(latest.FrameworkVersionRange, out var range) && range.HasLowerBound && range.MinVersion > current
            ? RemotePluginCatalogEntry.RequiresNewerOscova
            : RemotePluginCatalogEntry.Incompatible;
        return latest;
    }

    public async Task<IReadOnlyList<RemotePluginCatalogEntry>> GetReleasesAsync(string packageId, string frameworkVersion, CancellationToken cancellationToken)
    {
        var version = NuGetVersion.Parse(frameworkVersion);
        var releases = await GetReleasesFromSourceAsync(packageId, includePrerelease: false, cancellationToken).ConfigureAwait(false);
        return releases.Where(item => PluginFrameworkCompatibility.GetIncompatibility(item.FrameworkVersionRange, version) is null).ToArray();
    }

    private async Task<IReadOnlyList<RemotePluginCatalogEntry>> GetReleasesFromSourceAsync(string packageId, bool includePrerelease, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return [];
        }

        try
        {
            var repository = Repository.Factory.GetCoreV3(NuGetApplicationUpdateSource.ResolvePackageSource());
            var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The package source does not expose package metadata.");
            using var cache = new SourceCacheContext();
            var metadata = await resource.GetMetadataAsync(
                packageId.Trim(),
                includePrerelease,
                includeUnlisted: false,
                cache,
                NullLogger.Instance,
                cancellationToken).ConfigureAwait(false);

            return metadata
                .Where(item => includePrerelease || !item.Identity.Version.IsPrerelease)
                .OrderByDescending(item => item.Identity.Version)
                .Select(Map)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "NuGet plugin metadata lookup failed for package {PackageId}.", packageId);
            throw new NuGetPluginRegistryException("NuGet.org could not return plugin metadata.", ex);
        }
    }

    private static List<RemotePluginCatalogEntry> ParseSearchResponse(JsonDocument document)
    {
        var entries = new List<RemotePluginCatalogEntry>();
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!HasOscovaPluginPackageType(item))
            {
                continue;
            }

            entries.Add(MapSearchItem(item));
        }

        return entries;
    }

    // Defensive double-check: even after asking the server for packageType=OscovaPlugin, drop any rows
    // that do not declare it. Keeps the UI clean if the upstream filter ever regresses again.
    private static bool HasOscovaPluginPackageType(JsonElement item)
    {
        if (!item.TryGetProperty("packageTypes", out var packageTypes) || packageTypes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var typeNode in packageTypes.EnumerateArray())
        {
            if (typeNode.TryGetProperty("name", out var nameNode) &&
                nameNode.ValueKind == JsonValueKind.String &&
                string.Equals(nameNode.GetString(), PluginPackageType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static RemotePluginCatalogEntry MapSearchItem(JsonElement item)
    {
        var id = GetString(item, "id") ?? string.Empty;
        var title = GetString(item, "title");
        var authorsValue = item.TryGetProperty("authors", out var authorsNode) ? FormatAuthors(authorsNode) : string.Empty;

        return new RemotePluginCatalogEntry
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? id : title!,
            Version = GetString(item, "version") ?? string.Empty,
            Authors = authorsValue,
            Tags = item.TryGetProperty("tags", out var tags) ? ReadTags(tags) : [],
            Description = GetString(item, "description") ?? string.Empty,
            ProjectUrl = GetString(item, "projectUrl"),
            LicenseExpression = GetString(item, "licenseExpression") ?? GetString(item, "licenseUrl"),
            IconUrl = GetString(item, "iconUrl"),
            IsVerified = item.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True,
            DownloadCount = item.TryGetProperty("totalDownloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : null
        };
    }

    private static string FormatAuthors(JsonElement authorsNode)
    {
        if (authorsNode.ValueKind == JsonValueKind.String)
        {
            return authorsNode.GetString() ?? string.Empty;
        }

        if (authorsNode.ValueKind == JsonValueKind.Array)
        {
            return string.Join(", ", authorsNode
                .EnumerateArray()
                .Where(node => node.ValueKind == JsonValueKind.String)
                .Select(node => node.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return string.Empty;
    }

    private static string[] ReadTags(JsonElement tags) => tags.ValueKind switch
    {
        JsonValueKind.String => SplitTags(tags.GetString()),
        JsonValueKind.Array => tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String)
            .SelectMany(tag => SplitTags(tag.GetString())).ToArray(),
        _ => []
    };

    private static string[] SplitTags(string? tags) =>
        (tags ?? string.Empty).Split([' ', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    private static RemotePluginCatalogEntry Map(IPackageSearchMetadata metadata)
    {
        return new RemotePluginCatalogEntry
        {
            Id = metadata.Identity.Id,
            Title = string.IsNullOrWhiteSpace(metadata.Title) ? metadata.Identity.Id : metadata.Title,
            Version = metadata.Identity.Version.ToNormalizedString(),
            FrameworkVersionRange = PluginFrameworkCompatibility.GetDependencyRange(metadata.DependencySets) ?? string.Empty,
            Authors = metadata.Authors ?? string.Empty,
            Tags = SplitTags(metadata.Tags),
            Description = metadata.Description ?? string.Empty,
            ProjectUrl = metadata.ProjectUrl?.AbsoluteUri,
            LicenseExpression = metadata.LicenseMetadata?.LicenseExpression?.ToString(),
            IconUrl = metadata.IconUrl?.AbsoluteUri,
            DownloadCount = metadata.DownloadCount
        };
    }

    /// <summary>Checks installed package identities without discovering absent optional capabilities.</summary>
    public static async Task<IReadOnlyList<InstalledPluginUpdate>> FindInstalledUpdatesAsync(
        IPluginRegistryService registry, string pluginsRoot, string frameworkVersion, CancellationToken cancellationToken)
    {
        var checks = AgentPluginLoader.InspectDirectory(pluginsRoot)
            .Where(inspection => inspection.Manifest is not null)
            .Select(async inspection =>
            {
                var package = InstalledPluginPackage.Read(inspection);
                if (package is null || !NuGetVersion.TryParse(package.Version, out var installed)) { return null; }

                var releases = await registry.GetReleasesAsync(package.PackageId, frameworkVersion, cancellationToken).ConfigureAwait(false);
                var candidate = releases.FirstOrDefault(release => NuGetVersion.Parse(release.Version) > installed);
                return candidate is null ? null : new InstalledPluginUpdate(inspection.Manifest!.Id,
                    inspection.Manifest.Name, package.PackageId, package.Version, candidate.Version);
            });
        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        return results.Where(result => result is not null).Select(result => result!).ToArray();
    }
}
