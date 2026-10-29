using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Plugins;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Plugins.Registry;

/// <summary>Installs, updates, or removes a NuGet plugin and its current-platform runtime dependencies.</summary>
public sealed class NuGetPluginInstallService : IPluginInstallService
{
    private readonly ILogger<NuGetPluginInstallService> _logger;
    private readonly SourceRepository _repository;
    private readonly IReadOnlyList<PackageSource> _packageSources;
    private readonly string _pluginsRoot;
    private readonly string _bundledPackagesRoot;
    private readonly string _packageCacheRoot;

    /// <summary>
    /// Uses the selected feeds and exact bundled archives for plugin restoration.
    /// Restored packages are shared in <c>plugin-package-cache</c> beside the plugins root unless another cache folder is given.
    /// </summary>
    public NuGetPluginInstallService(
        ILogger<NuGetPluginInstallService> logger,
        string packageSource,
        string pluginsRoot,
        IReadOnlyList<PackageSource>? additionalPackageSources = null,
        string? bundledPackagesRoot = null,
        string? packageCacheRoot = null)
    {
        _logger = logger;
        _repository = Repository.Factory.GetCoreV3(packageSource);
        _packageSources = [_repository.PackageSource, .. additionalPackageSources ?? []];
        _pluginsRoot = Path.GetFullPath(pluginsRoot);
        _bundledPackagesRoot = Path.GetFullPath(
            bundledPackagesRoot ?? Path.Combine(AppContext.BaseDirectory, "bundled-plugins"));
        // Discovery scans the plugins root for manifests, and cached packages contain them, so the cache stays outside it.
        _packageCacheRoot = Path.GetFullPath(
            packageCacheRoot ?? Path.Combine(Path.GetDirectoryName(_pluginsRoot)!, PathUtil.PluginPackageCacheFolderName));
    }

    public async Task<string> InstallAsync(
        string packageId,
        string? packageVersion,
        string? expectedPluginId,
        CancellationToken cancellationToken,
        string? replacePluginId = null,
        string? replacePackageId = null,
        bool explicitSelection = true)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new ArgumentException("Package id is required.", nameof(packageId));
        }

        var normalizedPackageId = packageId.Trim();
        using var cache = new SourceCacheContext();

        // Marketplace installs select latest stable; unattended defaults supply a reviewed version.
        NuGetVersion version;
        if (string.IsNullOrWhiteSpace(packageVersion))
        {
            version = await ResolveLatestStableVersionAsync(
                normalizedPackageId,
                cache,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!NuGetVersion.TryParse(packageVersion.Trim(), out var parsedVersion))
        {
            throw new ArgumentException("Package version is invalid.", nameof(packageVersion));
        }
        else
        {
            version = parsedVersion;
        }

        var archiveName = $"{normalizedPackageId.ToLowerInvariant()}.{version.ToNormalizedString().ToLowerInvariant()}.nupkg";
        IReadOnlyList<PackageSource> sources = File.Exists(Path.Combine(_bundledPackagesRoot, archiveName))
            ? [new PackageSource(_bundledPackagesRoot)] : _packageSources;
        // Metadata admission precedes archive download, even for an explicitly chosen release.
        await ValidateMetadataAsync(normalizedPackageId, version, sources, cache, cancellationToken).ConfigureAwait(false);

        var stagingDirectory = Path.Combine(_pluginsRoot, $".staging-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var manifest = await NuGetPluginRestore.RestoreAsync(
                normalizedPackageId,
                version,
                sources,
                stagingDirectory,
                _packageCacheRoot,
                cancellationToken).ConfigureAwait(false);
            PruneOtherCachedVersions(normalizedPackageId, version);

            // Defaults are reviewed by manifest identity, so reject substitutions before discovery can see them.
            if (expectedPluginId is not null &&
                !string.Equals(manifest.Id, expectedPluginId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Package '{normalizedPackageId}' contained unexpected plugin id '{manifest.Id}'.",
                    nameof(expectedPluginId));
            }

            ValidateInspection(stagingDirectory, manifest.Id);
            // Package identity travels with the files through pending updates and promotion.
            await new InstalledPluginPackage(normalizedPackageId, version.ToNormalizedString()) { ExplicitSelection = explicitSelection }
                .WriteAsync(stagingDirectory, cancellationToken).ConfigureAwait(false);
            var pluginDirectory = ResolvePluginDirectory(manifest.Id);

            var installed = AgentPluginLoader.InspectDirectory(_pluginsRoot)
                .FirstOrDefault(item => string.Equals(item.Manifest?.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
            var owner = InstalledPluginPackage.Read(installed);
            var samePackage = owner is not null && string.Equals(owner.PackageId, normalizedPackageId, StringComparison.OrdinalIgnoreCase);
            var approvedReplacement = string.Equals(replacePluginId, manifest.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(replacePackageId, owner?.PackageId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (installed is not null && !samePackage && !approvedReplacement)
            {
                throw new PluginPackageReplacementRequiredException(manifest.Id, owner?.PackageId);
            }

            // Loaded plugin files cannot be replaced safely. Updates wait outside discovery until startup.
            var destinationDirectory = Directory.Exists(pluginDirectory)
                ? PendingPluginUpdateBootstrapper.ResolvePendingDirectory(_pluginsRoot, Path.GetFileName(pluginDirectory))
                : pluginDirectory;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationDirectory)!);

            // The move atomically claims the plugin id; a concurrent winner becomes a conflict.
            try
            {
                Directory.Move(stagingDirectory, destinationDirectory);
            }
            catch (IOException) when (Directory.Exists(destinationDirectory))
            {
                throw new PluginInstallConflictException($"Plugin '{manifest.Id}' already has an install or update in progress.");
            }

            return manifest.Id;
        }
        catch
        {
            DeletePartialDirectory(stagingDirectory);
            throw;
        }
    }

    private static async Task ValidateMetadataAsync(string packageId, NuGetVersion version,
        IReadOnlyList<PackageSource> sources, SourceCacheContext cache, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            var repository = Repository.Factory.GetCoreV3(source);
            var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The package source does not expose package metadata.");
            // An exact requested version is admitted even when unlisted, so recovery can reinstall a retired release.
            var releases = await resource.GetMetadataAsync(packageId, version.IsPrerelease, true, cache, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
            var release = releases.FirstOrDefault(item => item.Identity.Version.Equals(version));
            if (release is null) { continue; }

            var reason = PluginFrameworkCompatibility.GetIncompatibility(
                PluginFrameworkCompatibility.GetDependencyRange(release.DependencySets), PluginFrameworkCompatibility.CurrentVersion);
            if (reason is not null) { throw new ArgumentException($"Package '{packageId}' {version}: {reason}"); }

            return;
        }

        throw new NuGetPluginRegistryException($"Package '{packageId}' {version} was not found in the configured feeds.");
    }

    public void Uninstall(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var pluginDirectory = ResolvePluginDirectory(pluginId);

        // Remove update artifacts first so startup cannot restore an uninstalled plugin.
        foreach (var directory in new[]
        {
            PendingPluginUpdateBootstrapper.ResolvePendingDirectory(_pluginsRoot, Path.GetFileName(pluginDirectory)),
            PendingPluginUpdateBootstrapper.ResolveBackupDirectory(_pluginsRoot, Path.GetFileName(pluginDirectory)),
            pluginDirectory
        })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private async Task<NuGetVersion> ResolveLatestStableVersionAsync(
        string packageId,
        SourceCacheContext cache,
        CancellationToken cancellationToken)
    {
        var versions = new List<NuGetVersion>();
        try
        {
            foreach (var source in _packageSources)
            {
                var repository = Repository.Factory.GetCoreV3(source);
                var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The package source does not expose package metadata.");
                var releases = await resource.GetMetadataAsync(packageId, false, false, cache, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
                versions.AddRange(releases.Where(release => PluginFrameworkCompatibility.GetIncompatibility(
                    PluginFrameworkCompatibility.GetDependencyRange(release.DependencySets), PluginFrameworkCompatibility.CurrentVersion) is null)
                    .Select(release => release.Identity.Version));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new NuGetPluginRegistryException($"The configured feeds could not be queried for package '{packageId}'.", ex);
        }

        var version = versions
            .Where(candidate => !candidate.IsPrerelease)
            .OrderByDescending(candidate => candidate)
            .FirstOrDefault();

        return version ?? throw new NuGetPluginRegistryException($"Package '{packageId}' has no stable release compatible with Oscova.Agents {PluginFrameworkCompatibility.CurrentVersion} in the configured feeds.");
    }

    private static void ValidateInspection(string stagingDirectory, string expectedPluginId)
    {
        var inspections = AgentPluginLoader.InspectDirectory(stagingDirectory);
        var error = inspections
            .Select(inspection => inspection.Diagnostic)
            .FirstOrDefault(diagnostic => diagnostic?.Level == AgentPluginLoadDiagnosticLevel.Error);
        if (error is not null ||
            inspections.Count != 1 ||
            inspections[0].Manifest is null ||
            !string.Equals(inspections[0].Manifest?.Id, expectedPluginId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                error?.Message ?? "The plugin package must contain exactly one manifest matching its restored plugin id.");
        }
    }

    private string ResolvePluginDirectory(string manifestId)
    {
        var pluginDirectory = Path.GetFullPath(Path.Combine(_pluginsRoot, manifestId));
        if (PathHelper.ArePathsEqual(pluginDirectory, _pluginsRoot) ||
            !PathHelper.IsPathWithinDirectory(pluginDirectory, _pluginsRoot))
        {
            throw new ArgumentException("Plugin manifest Id must stay within the plugin directory.");
        }

        // Plugin identity keeps its installed directory spelling across case-only updates.
        return Directory.Exists(_pluginsRoot)
            ? Directory.EnumerateDirectories(_pluginsRoot).FirstOrDefault(directory =>
                string.Equals(Path.GetFileName(directory), manifestId, StringComparison.OrdinalIgnoreCase)) ?? pluginDirectory
            : pluginDirectory;
    }

    // ponytail: prunes only the plugin package itself; superseded dependency versions stay until the cache folder is cleared.
    private void PruneOtherCachedVersions(string packageId, NuGetVersion version)
    {
        var packageDirectory = Path.Combine(_packageCacheRoot, packageId.ToLowerInvariant());
        var keep = version.ToNormalizedString().ToLowerInvariant();
        foreach (var versionDirectory in Directory.EnumerateDirectories(packageDirectory))
        {
            if (string.Equals(Path.GetFileName(versionDirectory), keep, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(versionDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to prune cached plugin package {PackageDirectory}.", versionDirectory);
            }
        }
    }

    private void DeletePartialDirectory(string pluginDirectory)
    {
        try
        {
            if (Directory.Exists(pluginDirectory))
            {
                Directory.Delete(pluginDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete partial plugin install directory {PluginDirectory}.", pluginDirectory);
        }
    }
}
