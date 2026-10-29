using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyModel;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Plugins;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Plugins.Registry;

/// <summary>Restores one plugin package graph and materializes its selected runtime component.</summary>
internal static class NuGetPluginRestore
{
    private const string TargetFramework = "net10.0";
    private const string PluginPackageType = "OscovaPlugin";
    private const long MaxRootPackageBytes = 25L * 1024L * 1024L;
    private const int MaxPackageCount = 128;
    private const long MaxRestoreCacheBytes = 4L * 1024L * 1024L * 1024L;
    private const int MaxMaterializedFileCount = 4_096;
    private const long MaxMaterializedBytes = 1L * 1024L * 1024L * 1024L;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly NuGetFramework Framework = NuGetFramework.ParseFolder(TargetFramework);

    /// <summary>Restores and writes the loadable component into an existing staging directory.</summary>
    internal static async Task<AgentPluginManifest> RestoreAsync(
        string packageId,
        NuGetVersion version,
        IReadOnlyList<PackageSource> packageSources,
        string stagingDirectory,
        string packagesRoot,
        CancellationToken cancellationToken)
    {
        var restoreRoot = Path.Combine(stagingDirectory, ".restore");
        var rid = AgentPluginLoader.SupportedRuntimeIdentifier;

        var rootPackage = await GetRootPackageAsync(packageId, version, packageSources, packagesRoot, restoreRoot, cancellationToken).ConfigureAwait(false);
        ReadAndValidateRootPackage(rootPackage, stagingDirectory);

        var lockFile = await RestoreGraphAsync(
            packageId,
            version,
            packageSources,
            packagesRoot,
            restoreRoot,
            rid,
            cancellationToken).ConfigureAwait(false);

        var target = lockFile.GetTarget(Framework, rid)
            ?? throw new ArgumentException($"The plugin dependency graph did not contain one {TargetFramework}/{rid} target.");
        ValidateGraphAndCache(lockFile, target, packagesRoot);
        var manifest = ReadAndValidateRootPackage(new VersionFolderPathResolver(packagesRoot).GetPackageFilePath(packageId, version), stagingDirectory);
        var runtimeLibraries = await MaterializeLibrariesAsync(
            lockFile,
            target,
            packagesRoot,
            stagingDirectory,
            packageId,
            rid,
            manifest,
            cancellationToken).ConfigureAwait(false);

        WriteDependencyContext(stagingDirectory, manifest.Assembly, rid, runtimeLibraries);
        if (Directory.Exists(restoreRoot))
        {
            Directory.Delete(restoreRoot, recursive: true);
        }

        return manifest;
    }

    private static async Task<string> GetRootPackageAsync(
        string packageId,
        NuGetVersion version,
        IReadOnlyList<PackageSource> packageSources,
        string packagesRoot,
        string restoreRoot,
        CancellationToken cancellationToken)
    {
        var cachedPackage = new VersionFolderPathResolver(packagesRoot).GetPackageFilePath(packageId, version);
        if (File.Exists(cachedPackage)) { return cachedPackage; }

        Directory.CreateDirectory(restoreRoot);
        var packagePath = Path.Combine(restoreRoot, "root.nupkg");
        using var cache = new SourceCacheContext();
        try
        {
            foreach (var source in packageSources)
            {
                var repository = new SourceRepository(source, Repository.Provider.GetCoreV3());
                var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The package source does not support package downloads.");
                await using var destination = File.Create(packagePath);
                if (await resource.CopyNupkgToStreamAsync(packageId, version, destination, cache, NullLogger.Instance, cancellationToken).ConfigureAwait(false))
                {
                    return packagePath;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new NuGetPluginRegistryException($"Package '{packageId}' could not be downloaded.", ex);
        }

        throw new NuGetPluginRegistryException($"Package '{packageId}' could not be found in the configured feeds.");
    }

    private static async Task<LockFile> RestoreGraphAsync(
        string packageId,
        NuGetVersion version,
        IReadOnlyList<PackageSource> packageSources,
        string packagesRoot,
        string restoreRoot,
        string rid,
        CancellationToken cancellationToken)
    {
        // NuGet identifies restore requests by a project-shaped path; no project file is created.
        var syntheticProjectPath = Path.Combine(restoreRoot, "plugin-restore.csproj");
        var frameworkInfo = new TargetFrameworkInformation
        {
            FrameworkName = Framework,
            TargetAlias = TargetFramework,
            Dependencies =
            [
                new LibraryDependency
                {
                    LibraryRange = new LibraryRange(
                        packageId,
                        new VersionRange(version, includeMinVersion: true, version, includeMaxVersion: true),
                        LibraryDependencyTarget.Package)
                }
            ]
        };
        var packageSpec = new PackageSpec([frameworkInfo])
        {
            Name = "OscovaPluginRestore",
            FilePath = syntheticProjectPath,
            RestoreMetadata = new ProjectRestoreMetadata
            {
                ProjectUniqueName = syntheticProjectPath,
                ProjectName = "OscovaPluginRestore",
                ProjectPath = syntheticProjectPath,
                OutputPath = Path.Combine(restoreRoot, ".restore-output"),
                ProjectStyle = ProjectStyle.PackageReference
            }
        };
        packageSpec.RestoreMetadata.OriginalTargetFrameworks.Add(TargetFramework);
        packageSpec.RestoreMetadata.TargetFrameworks.Add(
            new ProjectRestoreMetadataFrameworkInfo(Framework) { TargetAlias = TargetFramework });

        // The running host supplies this framework, so NuGet must not fetch another framework graph.
        var frameworkPath = Path.Combine(restoreRoot, "Oscova.Agents.csproj");
        var hostFramework = new PackageSpec([new TargetFrameworkInformation { FrameworkName = Framework, TargetAlias = TargetFramework }])
        {
            Name = "Oscova.Agents",
            Version = PluginFrameworkCompatibility.CurrentVersion,
            FilePath = frameworkPath,
            RestoreMetadata = new ProjectRestoreMetadata
            {
                ProjectUniqueName = frameworkPath,
                ProjectName = "Oscova.Agents",
                ProjectPath = frameworkPath,
                OutputPath = Path.Combine(restoreRoot, ".framework-output"),
                ProjectStyle = ProjectStyle.PackageReference
            }
        };
        hostFramework.RestoreMetadata.OriginalTargetFrameworks.Add(TargetFramework);
        hostFramework.RestoreMetadata.TargetFrameworks.Add(new ProjectRestoreMetadataFrameworkInfo(Framework) { TargetAlias = TargetFramework });
        packageSpec.RestoreMetadata.TargetFrameworks[0].ProjectReferences.Add(new ProjectRestoreReference
        {
            ProjectUniqueName = frameworkPath,
            ProjectPath = frameworkPath
        });
        var dependencyGraph = new DependencyGraphSpec();
        dependencyGraph.AddProject(hostFramework);
        dependencyGraph.AddProject(packageSpec);
        dependencyGraph.AddRestore(syntheticProjectPath);

        using var cache = new SourceCacheContext();
        var sourceProvider = new PackageSourceProvider(NullSettings.Instance, packageSources);
        var restoreArgs = new RestoreArgs
        {
            CacheContext = cache,
            CachingSourceProvider = new CachingSourceProvider(sourceProvider),
            DisableParallel = false,
            GlobalPackagesFolder = packagesRoot,
            Sources = packageSources.Select(source => source.Source).ToList(),
            Log = NullLogger.Instance,
            PreLoadedRequestProviders =
            [
                new DependencyGraphSpecRequestProvider(
                    new RestoreCommandProvidersCache(),
                    dependencyGraph,
                    NullSettings.Instance)
            ]
        };
        restoreArgs.Runtimes.Add(rid);

        try
        {
            // NuGet owns dependency, framework, and RID selection; Oscova consumes the resulting lock graph.
            var requests = await RestoreRunner.GetRequests(restoreArgs).ConfigureAwait(false);
            if (requests.Count != 1)
            {
                throw new NuGetPluginRegistryException($"Package '{packageId}' produced an invalid restore request.");
            }

            // RestoreRunner drops cancellation for non-committing restores, so execute its single request directly.
            var result = await new RestoreCommand(requests[0].Request)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                var error = result.LogMessages.FirstOrDefault(message => message.Level == LogLevel.Error);
                var detail = error is null ? string.Empty : $" {error.Code}: {error.Message}";
                throw new NuGetPluginRegistryException($"Package '{packageId}' dependencies could not be restored.{detail}");
            }

            return result.LockFile;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NuGetPluginRegistryException)
        {
            throw new NuGetPluginRegistryException($"Package '{packageId}' dependencies could not be restored.", ex);
        }
    }

    private static void ValidateGraphAndCache(LockFile lockFile, LockFileTarget target, string packagesRoot)
    {
        var packageCount = target.Libraries.Count(library => string.Equals(library.Type, "package", StringComparison.OrdinalIgnoreCase));
        if (packageCount > MaxPackageCount)
        {
            throw new ArgumentException($"The plugin dependency graph exceeds the {MaxPackageCount}-package limit.");
        }

        // The shared cache holds other plugins' packages, so only this graph's package folders count.
        var packageDirectories = lockFile.Libraries
            .Where(library => string.Equals(library.Type, "package", StringComparison.OrdinalIgnoreCase))
            .Select(library => ResolveContainedPath(packagesRoot, library.Path, "package directory"))
            .Where(Directory.Exists);
        long cacheBytes = 0;
        foreach (var path in packageDirectories.SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)))
        {
            var length = new FileInfo(path).Length;
            if (length > MaxRestoreCacheBytes - cacheBytes)
            {
                throw new ArgumentException("The plugin dependency restore exceeds the 4 GB cache limit.");
            }

            cacheBytes += length;
        }
    }

    private static AgentPluginManifest ReadAndValidateRootPackage(
        string packagePath,
        string stagingDirectory)
    {
        if (!File.Exists(packagePath))
        {
            throw new ArgumentException("The restored plugin package archive was not found.");
        }

        if (new FileInfo(packagePath).Length > MaxRootPackageBytes)
        {
            throw new ArgumentException("The remote plugin package exceeds the 25 MB size limit.");
        }

        using var archive = new PackageArchiveReader(packagePath);
        ValidatePackageType(archive);
        var manifestPath = archive.GetFiles().FirstOrDefault(path =>
            string.Equals(path, AgentPluginLoader.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("The plugin package must contain oscova-plugin.json at the package root.");

        AgentPluginManifest manifest;
        try
        {
            using var manifestStream = archive.GetStream(manifestPath);
            manifest = JsonSerializer.Deserialize<AgentPluginManifest>(manifestStream, JsonOptions)
                ?? throw new ArgumentException("Plugin manifest could not be read.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Plugin manifest could not be read.", ex);
        }

        ValidateManifestForInstall(manifest);
        var reason = PluginFrameworkCompatibility.GetIncompatibility(manifest.FrameworkVersionRange, PluginFrameworkCompatibility.CurrentVersion);
        if (reason is not null) { throw new ArgumentException(reason); }

        if (!manifest.SupportsRuntimeIdentifier(AgentPluginLoader.SupportedRuntimeIdentifier))
        {
            throw new PluginPlatformNotSupportedException(manifest.Id, AgentPluginLoader.SupportedRuntimeIdentifier);
        }

        var dependencyRange = PluginFrameworkCompatibility.GetDependencyRange(archive.GetPackageDependencies());
        if (dependencyRange is null || !VersionRange.TryParse(dependencyRange, out var publishedRange) ||
            !VersionRange.Parse(manifest.FrameworkVersionRange).Equals(publishedRange))
        {
            throw new ArgumentException("Plugin frameworkVersionRange must match its NuGet Oscova.Agents dependency range.");
        }

        using var source = archive.GetStream(manifestPath);
        using var destination = File.Create(Path.Combine(stagingDirectory, AgentPluginLoader.ManifestFileName));
        source.CopyTo(destination);
        return manifest;
    }

    private static void ValidatePackageType(PackageArchiveReader archive)
    {
        if (!archive.GetFiles().Any(path =>
                path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains('/', StringComparison.Ordinal) &&
                !path.Contains('\\', StringComparison.Ordinal)))
        {
            throw new ArgumentException("The plugin package must include a NuGet manifest.");
        }

        IReadOnlyList<PackageType> packageTypes;
        try
        {
            packageTypes = archive.GetPackageTypes();
        }
        catch (Exception ex) when (ex is PackagingException or System.Xml.XmlException)
        {
            throw new ArgumentException("The plugin package NuGet manifest is invalid.", ex);
        }

        if (!packageTypes.Any(packageType =>
                string.Equals(packageType.Name, PluginPackageType, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"The plugin package must declare the {PluginPackageType} package type.");
        }
    }

    private static void ValidateManifestForInstall(AgentPluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new ArgumentException("Plugin manifest must set Id.");
        }

        if (!string.Equals(manifest.Id, manifest.Id.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Plugin manifest Id must not contain leading or trailing whitespace.");
        }

        if (manifest.Id.Contains(':'))
        {
            throw new ArgumentException("Plugin manifest Id must not contain ':' because it separates credential namespaces.");
        }

        if (manifest.Id.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || Path.IsPathRooted(manifest.Id))
        {
            throw new ArgumentException("Plugin manifest Id must be a folder-safe identifier.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            throw new ArgumentException("Plugin manifest must set Assembly.");
        }

        if (!string.Equals(manifest.Assembly, Path.GetFileName(manifest.Assembly), StringComparison.Ordinal))
        {
            throw new ArgumentException("Plugin manifest Assembly must be a file name.");
        }

        if (manifest.DependencyAssets is null)
        {
            throw new ArgumentException("Plugin manifest DependencyAssets must be an array.");
        }

        foreach (var asset in manifest.DependencyAssets)
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.PackageId) || string.IsNullOrWhiteSpace(asset.Path))
            {
                throw new ArgumentException("Each dependency asset must set PackageId and Path.");
            }

            if (asset.Path.Contains('\\') || asset.Path.Split('/').Any(segment => segment is "." or ".."))
            {
                throw new ArgumentException("Dependency asset Path must use forward slashes without '.' or '..' segments.");
            }

            if (asset.Rid is not (null or "win-x64" or "linux-x64" or "osx-x64" or "osx-arm64"))
            {
                throw new ArgumentException("Dependency asset Rid must be win-x64, linux-x64, osx-x64, osx-arm64, or omitted.");
            }
        }
    }

    private static async Task<IReadOnlyList<RuntimeLibrary>> MaterializeLibrariesAsync(
        LockFile lockFile,
        LockFileTarget target,
        string packagesRoot,
        string stagingDirectory,
        string rootPackageId,
        string rid,
        AgentPluginManifest manifest,
        CancellationToken cancellationToken)
    {
        var managedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Native collision state spans every package in the restored graph.
        var rootNativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootNativeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencyNativeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runtimeLibraries = new List<RuntimeLibrary>();
        var copiedAssets = new Dictionary<string, string>(StringComparer.Ordinal);
        var entryAssemblyCount = 0;
        var materializedFileCount = 0;
        long materializedBytes = 0;

        // Declarations retain files from the resolved graph and never add another package or destination.
        foreach (var asset in manifest.DependencyAssets)
        {
            var dependency = target.GetTargetLibrary(asset.PackageId);
            if (dependency is null || !string.Equals(dependency.Type, "package", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(asset.PackageId, rootPackageId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Dependency asset PackageId '{asset.PackageId}' must name a restored dependency package.");
            }

            _ = ResolveContainedPath(packagesRoot, asset.Path, "dependency asset");
        }

        foreach (var targetLibrary in target.Libraries.Where(library => string.Equals(library.Type, "package", StringComparison.OrdinalIgnoreCase)))
        {
            if (targetLibrary.Version is null || string.IsNullOrWhiteSpace(targetLibrary.Name))
            {
                throw new ArgumentException("The plugin dependency graph contains an invalid package identity.");
            }

            if (!string.Equals(targetLibrary.Name, rootPackageId, StringComparison.OrdinalIgnoreCase) &&
                targetLibrary.PackageType.Any(packageType =>
                    string.Equals(packageType.Name, PluginPackageType, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Plugin package '{targetLibrary.Name}' cannot be installed as a dependency.");
            }

            var lockLibrary = lockFile.GetLibrary(targetLibrary.Name, targetLibrary.Version);
            if (lockLibrary is null || string.IsNullOrWhiteSpace(lockLibrary.Path))
            {
                throw new ArgumentException($"The restored package '{targetLibrary.Name}' could not be located.");
            }

            var packageRoot = ResolveContainedPath(packagesRoot, lockLibrary.Path, "package directory");
            if (!Directory.Exists(packageRoot))
            {
                throw new ArgumentException($"The restored package '{targetLibrary.Name}' directory was not found.");
            }

            var runtimeFiles = new List<RuntimeFile>();
            var nativeFiles = new List<RuntimeFile>();
            var resourceFiles = new List<ResourceAssembly>();
            // One package classification applies to every native asset in this library.
            var isRootPackage = string.Equals(
                targetLibrary.Name,
                rootPackageId,
                StringComparison.OrdinalIgnoreCase);

            foreach (var asset in targetLibrary.RuntimeAssemblies.Where(IsMaterialAsset))
            {
                var sourcePath = ResolveAssetPath(packageRoot, asset.Path);
                string assemblyName;
                try
                {
                    assemblyName = AssemblyName.GetAssemblyName(sourcePath).Name
                        ?? throw new ArgumentException($"Managed asset '{asset.Path}' has no assembly name.");
                }
                catch (BadImageFormatException ex)
                {
                    throw new ArgumentException($"Managed asset '{asset.Path}' is not a valid assembly.", ex);
                }

                if (AgentPluginLoader.SharedAssemblyNames.Contains(assemblyName))
                {
                    continue;
                }

                if (!managedNames.Add(assemblyName))
                {
                    throw new ArgumentException($"The plugin dependency graph contains duplicate managed assembly '{assemblyName}'.");
                }

                var isEntryAssembly =
                    string.Equals(targetLibrary.Name, rootPackageId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(asset.Path), manifest.Assembly, StringComparison.OrdinalIgnoreCase);
                if (isEntryAssembly)
                {
                    entryAssemblyCount++;
                }

                var installedRelativePath = isEntryAssembly
                    ? manifest.Assembly
                    : GetPackageScopedPath(lockLibrary.Path, asset.Path);
                await CopyAssetAsync(sourcePath, installedRelativePath).ConfigureAwait(false);
                asset.Properties.TryGetValue("assemblyVersion", out var assemblyVersion);
                asset.Properties.TryGetValue("fileVersion", out var fileVersion);
                // localPath points AssemblyDependencyResolver at the package-scoped installed copy.
                runtimeFiles.Add(new RuntimeFile(
                    asset.Path,
                    assemblyVersion,
                    fileVersion,
                    PathHelper.ToSlashSeparatedRelativePath(installedRelativePath)));
            }

            foreach (var asset in targetLibrary.NativeLibraries.Where(IsMaterialAsset))
            {
                // Preserve root variants while rejecting graph-wide ambiguity.
                var fileName = Path.GetFileName(asset.Path);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    throw new ArgumentException($"The plugin dependency graph contains duplicate native asset '{fileName}'.");
                }

                var sourcePath = ResolveAssetPath(packageRoot, asset.Path);
                var installedRelativePath = isRootPackage
                    ? PathHelper.ToPlatformRelativePath(asset.Path)
                    : GetPackageScopedPath(lockLibrary.Path, asset.Path);
                var installedPathKey = PathHelper.ToSlashSeparatedRelativePath(installedRelativePath);
                var hasCollision = isRootPackage
                    ? dependencyNativeNames.Contains(fileName) || !rootNativePaths.Add(installedPathKey)
                    : rootNativeNames.Contains(fileName) || !dependencyNativeNames.Add(fileName);
                if (hasCollision)
                {
                    throw new ArgumentException($"The plugin dependency graph contains duplicate native asset '{fileName}'.");
                }

                if (isRootPackage)
                {
                    rootNativeNames.Add(fileName);
                }

                await CopyAssetAsync(sourcePath, installedRelativePath).ConfigureAwait(false);
                nativeFiles.Add(new RuntimeFile(
                    asset.Path,
                    assemblyVersion: null,
                    fileVersion: null,
                    PathHelper.ToSlashSeparatedRelativePath(installedRelativePath)));
            }

            foreach (var asset in targetLibrary.ResourceAssemblies.Where(IsMaterialAsset))
            {
                if (!asset.Properties.TryGetValue("locale", out var locale) || string.IsNullOrWhiteSpace(locale))
                {
                    throw new ArgumentException($"Resource asset '{asset.Path}' does not declare a locale.");
                }

                var resourceKey = $"{locale}/{Path.GetFileName(asset.Path)}";
                if (!resourceNames.Add(resourceKey))
                {
                    throw new ArgumentException($"The plugin dependency graph contains duplicate resource assembly '{resourceKey}'.");
                }

                var sourcePath = ResolveAssetPath(packageRoot, asset.Path);
                var installedRelativePath = GetPackageScopedPath(lockLibrary.Path, asset.Path);
                await CopyAssetAsync(sourcePath, installedRelativePath).ConfigureAwait(false);
                resourceFiles.Add(new ResourceAssembly(
                    asset.Path,
                    locale,
                    PathHelper.ToSlashSeparatedRelativePath(installedRelativePath)));
            }

            if (!isRootPackage)
            {
                // Package build targets are not executed; copy only declared dependency content
                // through the same guarded materializer used by runtime assets.
                foreach (var contentPath in lockLibrary.Files.Where(path =>
                    path.StartsWith("content/", StringComparison.OrdinalIgnoreCase) &&
                    IsMaterialPath(path)))
                {
                    var sourcePath = ResolveAssetPath(packageRoot, contentPath);
                    await CopyAssetAsync(
                        sourcePath,
                        GetPackageScopedPath(lockLibrary.Path, contentPath)).ConfigureAwait(false);
                }

                foreach (var declaration in manifest.DependencyAssets.Where(asset =>
                    string.Equals(asset.PackageId, targetLibrary.Name, StringComparison.OrdinalIgnoreCase) &&
                    (asset.Rid is null || string.Equals(asset.Rid, rid, StringComparison.Ordinal))))
                {
                    var isDirectory = declaration.Path.EndsWith("/", StringComparison.Ordinal);
                    var files = lockLibrary.Files.Where(path => IsMaterialPath(path) &&
                        (isDirectory ? path.StartsWith(declaration.Path, StringComparison.Ordinal) :
                            string.Equals(path, declaration.Path, StringComparison.Ordinal))).ToArray();
                    if (files.Length == 0)
                    {
                        throw new ArgumentException($"Dependency asset Path '{declaration.Path}' was not found in '{targetLibrary.Name}'.");
                    }

                    foreach (var path in files)
                    {
                        await CopyAssetAsync(ResolveAssetPath(packageRoot, path),
                            GetPackageScopedPath(lockLibrary.Path, path)).ConfigureAwait(false);
                    }
                }
            }

            var dependencies = targetLibrary.Dependencies.Select(dependency =>
            {
                var selected = target.GetTargetLibrary(dependency.Id);
                if (selected?.Version is null)
                {
                    throw new ArgumentException($"Dependency '{dependency.Id}' was not selected in the plugin restore graph.");
                }

                return new Dependency(dependency.Id, selected.Version.ToNormalizedString());
            }).ToArray();

            // Empty, not null: DependencyContextWriter recognizes an empty RID as the default asset group.
            runtimeLibraries.Add(new RuntimeLibrary(
                "package",
                targetLibrary.Name,
                targetLibrary.Version.ToNormalizedString(),
                string.IsNullOrWhiteSpace(lockLibrary.Sha512) ? null : $"sha512-{lockLibrary.Sha512}",
                [new RuntimeAssetGroup(string.Empty, runtimeFiles)],
                [new RuntimeAssetGroup(string.Empty, nativeFiles)],
                resourceFiles,
                dependencies,
                serviceable: true,
                lockLibrary.Path,
                hashPath: null));
        }

        if (entryAssemblyCount != 1)
        {
            throw new ArgumentException($"The plugin package must contain {manifest.Assembly} as a selected {TargetFramework} runtime asset.");
        }

        return runtimeLibraries;

        async Task CopyAssetAsync(string sourcePath, string installedRelativePath)
        {
            var destinationPath = ResolveContainedPath(stagingDirectory, installedRelativePath, "installed asset");
            if (copiedAssets.TryGetValue(destinationPath, out var previousSource))
            {
                if (!string.Equals(previousSource, sourcePath, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Plugin assets collide at '{installedRelativePath}'.");
                }
                // Explicit declarations can overlap the ordinary asset groups without copying twice.
                return;
            }

            var sourceInfo = new FileInfo(sourcePath);
            if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException($"Plugin asset '{sourceInfo.Name}' must not be a link.");
            }

            if (materializedFileCount >= MaxMaterializedFileCount)
            {
                throw new ArgumentException($"The plugin dependency graph exceeds the {MaxMaterializedFileCount}-file limit.");
            }

            if (sourceInfo.Length > MaxMaterializedBytes - materializedBytes)
            {
                throw new ArgumentException("The plugin dependency graph exceeds the 1 GB installed-size limit.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            copiedAssets.Add(destinationPath, sourcePath);
            materializedFileCount++;
            materializedBytes += sourceInfo.Length;
        }
    }

    private static bool IsMaterialAsset(LockFileItem asset) => IsMaterialPath(asset.Path);

    private static bool IsMaterialPath(string path)
    {
        return !string.Equals(Path.GetFileName(path), "_._", StringComparison.Ordinal);
    }

    private static string ResolveAssetPath(string packageRoot, string packageRelativePath)
    {
        var sourcePath = ResolveContainedPath(packageRoot, packageRelativePath, "plugin asset");
        if (!File.Exists(sourcePath))
        {
            throw new ArgumentException($"Plugin asset '{packageRelativePath}' was not found.");
        }

        return sourcePath;
    }

    private static string ResolveContainedPath(string root, string relativePath, string description)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, PathHelper.ToPlatformRelativePath(relativePath)));
        if (Path.IsPathRooted(relativePath) || !PathHelper.IsPathWithinDirectory(fullPath, normalizedRoot))
        {
            throw new ArgumentException($"The plugin {description} path must stay within its expected directory.");
        }

        return fullPath;
    }

    private static string GetPackageScopedPath(string lockLibraryPath, string assetPath)
    {
        return Path.Combine(
            "packages",
            PathHelper.ToPlatformRelativePath(lockLibraryPath),
            PathHelper.ToPlatformRelativePath(assetPath));
    }

    private static void WriteDependencyContext(
        string stagingDirectory,
        string entryAssembly,
        string rid,
        IReadOnlyList<RuntimeLibrary> runtimeLibraries)
    {
        var dependencyContext = new DependencyContext(
            new TargetInfo(Framework.DotNetFrameworkName, rid, runtimeSignature: null, isPortable: false),
            CompilationOptions.Default,
            compileLibraries: [],
            runtimeLibraries,
            runtimeGraph: []);
        var dependencyPath = Path.ChangeExtension(Path.Combine(stagingDirectory, entryAssembly), ".deps.json");
        using var dependencyStream = File.Create(dependencyPath);
        new DependencyContextWriter().Write(dependencyContext, dependencyStream);
    }
}
