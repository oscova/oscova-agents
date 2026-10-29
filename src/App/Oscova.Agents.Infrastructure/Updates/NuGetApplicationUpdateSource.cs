using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Oscova.Agents.Infrastructure.Updates;

/// <summary>Reads listed stable releases of the Oscova application package.</summary>
public sealed class NuGetApplicationUpdateSource(ILogger<NuGetApplicationUpdateSource> logger)
{
    public const string PackageId = "Oscova.Agents.App";
    public const string OfficialFeedUrl = "https://api.nuget.org/v3/index.json";
    public const string FrameworkVersionTagPrefix = "oscova-framework-version-";

    /// <summary>Reads the exact target's build-stamped framework version without downloading its archive.</summary>
    public static async Task<string> GetFrameworkVersionAsync(string appVersion, CancellationToken cancellationToken)
    {
        var release = (await GetMetadataAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Identity.Version.Equals(NuGetVersion.Parse(appVersion)));
        var tokens = (release?.Tags ?? string.Empty).Split([' ', ';', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(tag => tag.StartsWith(FrameworkVersionTagPrefix, StringComparison.Ordinal)).ToArray();
        if (tokens.Length != 1 || !NuGetVersion.TryParse(tokens[0][FrameworkVersionTagPrefix.Length..], out var framework))
        {
            throw new InvalidDataException("The selected App release must publish exactly one valid oscova-framework-version-<version> tag. Plugin compatibility preparation is unavailable. Ask the publisher to correct its release metadata.");
        }

        return framework.ToNormalizedString();
    }

    /// <summary>Returns the newest listed stable package version.</summary>
    public async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            return metadata
                .Select(item => item.Identity.Version)
                .Where(version => !version.IsPrerelease)
                .OrderByDescending(version => version)
                .FirstOrDefault()
                ?.ToNormalizedString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Application update lookup failed for package {PackageId}.", PackageId);
            throw;
        }
    }

    /// <summary>Checks whether an exact stable target remains available from the selected source.</summary>
    public async Task<bool> IsVersionListedAsync(string version, CancellationToken cancellationToken)
    {
        if (!NuGetVersion.TryParse(version, out var target) || target.IsPrerelease)
        {
            return false;
        }

        try
        {
            var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            return metadata.Any(item => item.Identity.Version == target && !item.Identity.Version.IsPrerelease);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Application update target lookup failed for package {PackageId} version {Version}.", PackageId, version);
            throw;
        }
    }

    /// <summary>Returns the fixed production feed or the gated integration-test folder.</summary>
    public static string ResolvePackageSource()
    {
        return UpdateTestSources.GetPackageDirectory() ?? OfficialFeedUrl;
    }

    private static async Task<IReadOnlyList<IPackageSearchMetadata>> GetMetadataAsync(CancellationToken cancellationToken)
    {
        var repository = Repository.Factory.GetCoreV3(ResolvePackageSource());
        var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The package source does not expose package metadata.");
        using var cache = new SourceCacheContext();
        var metadata = await resource.GetMetadataAsync(
            PackageId,
            includePrerelease: false,
            includeUnlisted: false,
            cache,
            NullLogger.Instance,
            cancellationToken).ConfigureAwait(false);
        return metadata.ToArray();
    }
}
