using System.Text.Json;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Oscova.Agents.Plugins.Tests.LocalSpeech;

public sealed class LocalSpeechPluginPackageTests
{
    private const string PackageId = "Oscova.Agents.Plugins.LocalSpeech";
    private const string PackageVersion = "2.0.3";
    private const string NativePackageVersion = "2.0.0";
    private const string PluginId = "oscova.local-speech";
    private const long MaxPackageBytes = 26_214_400;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Package_ContainsReviewedManifestDependenciesRuntimeTreesAndLicenses()
    {
        var packagePath = GetPackagePath();
        using var archive = new PackageArchiveReader(packagePath);
        var files = archive.GetFiles().Select(NormalizePath).ToHashSet(StringComparer.Ordinal);
        var packageTypes = archive.GetPackageTypes();
        using var nuspecStream = archive.GetNuspec();
        var dependencies = new NuspecReader(nuspecStream).GetDependencyGroups()
            .SelectMany(group => group.Packages)
            .ToList();
        using var manifestStream = archive.GetStream(AgentPluginLoader.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<AgentPluginManifest>(manifestStream, JsonOptions);

        Assert.Contains(packageTypes, packageType => packageType.Name == "OscovaPlugin");
        Assert.NotNull(manifest);
        Assert.Equal(PluginId, manifest.Id);
        Assert.Equal(PackageVersion, manifest.Version);
        Assert.Equal("Oscova.Agents.Plugins.LocalSpeech.dll", manifest.Assembly);
        Assert.Contains("lib/net10.0/Oscova.Agents.Plugins.LocalSpeech.dll", files);

        AssertExactDependency(dependencies, "Whisper.net", "1.9.1");
        AssertExactDependency(dependencies, "KokoroSharp", "0.8.4");
        AssertExactDependency(dependencies, "NAudio", "3.0.1");
        AssertExactDependency(dependencies, "Microsoft.ML.OnnxRuntime", "1.29.0");
        Assert.DoesNotContain(dependencies, dependency => dependency.Id == "Whisper.net.Runtime");
        Assert.DoesNotContain(dependencies, dependency => dependency.Id.Contains("Cuda", StringComparison.OrdinalIgnoreCase) ||
            dependency.Id == "Microsoft.ML.OnnxRuntime.Gpu");

        Assert.Contains("licenses/Whisper.net-LICENSE.txt", files);
        Assert.Contains("licenses/whisper.cpp-LICENSE.txt", files);
        var runtimeRoots = files
            .Where(path => path.StartsWith("runtimes/", StringComparison.Ordinal))
            .Select(path => path.Split('/')[1])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["linux-x64", "osx-arm64", "win-x64"], runtimeRoots);
        foreach (var (outerRid, innerRuntime, entryLibrary) in new[]
                 {
                     ("win-x64", "win-x64", "whisper.dll"),
                     ("linux-x64", "linux-x64", "libwhisper.so"),
                     ("osx-arm64", "macos-arm64", "libwhisper.dylib")
                 })
        {
            var prefix = $"runtimes/{outerRid}/native/runtimes/{innerRuntime}/";
            var nativeFiles = files.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(nativeFiles);
            Assert.Contains(prefix + entryLibrary, nativeFiles);
        }
        Assert.DoesNotContain(files, path =>
            path.StartsWith("runtimes/osx-arm64/native/runtimes/osx-arm64/", StringComparison.Ordinal));
        Assert.DoesNotContain(files, path =>
            path.Contains("cuda", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("vulkan", StringComparison.OrdinalIgnoreCase));
        Assert.True(new FileInfo(packagePath).Length <= MaxPackageBytes);
    }

    private static void AssertExactDependency(IReadOnlyList<PackageDependency> dependencies, string id, string version)
    {
        var dependency = Assert.Single(dependencies, item => item.Id == id);
        var expected = NuGetVersion.Parse(version);
        Assert.Equal(expected, dependency.VersionRange.MinVersion);
        Assert.Equal(expected, dependency.VersionRange.MaxVersion);
        Assert.True(dependency.VersionRange.IsMinInclusive);
        Assert.True(dependency.VersionRange.IsMaxInclusive);
    }

    [Fact]
    public void CudaPackages_ContainRidOnlyPluginAndOrdinaryNativeDependencies()
    {
        var path = GetPackagePath(PackageId + ".Cuda");
        using var archive = new PackageArchiveReader(path);
        var files = archive.GetFiles().Select(NormalizePath).ToHashSet(StringComparer.Ordinal);
        Assert.True(new FileInfo(path).Length <= MaxPackageBytes);
        Assert.Contains(archive.GetPackageTypes(), type => type.Name == "OscovaPlugin");
        Assert.DoesNotContain(files, file => file.StartsWith("lib/", StringComparison.Ordinal));
        Assert.Equal(
            ["runtimes/linux-x64/lib/net10.0/Oscova.Agents.Plugins.LocalSpeech.dll", "runtimes/win-x64/lib/net10.0/Oscova.Agents.Plugins.LocalSpeech.dll"],
            files.Where(file => file.EndsWith(".dll", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        using var manifestStream = archive.GetStream(AgentPluginLoader.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<AgentPluginManifest>(manifestStream, JsonOptions);
        Assert.Equal(PluginId, manifest!.Id);
        Assert.Equal(PackageVersion, manifest.Version);
        Assert.Equal("Oscova Local Speech NVIDIA CUDA", manifest.Name);
        using var nuspec = archive.GetNuspec();
        var dependencies = new NuspecReader(nuspec).GetDependencyGroups().SelectMany(group => group.Packages).ToList();
        Assert.Equal(6, dependencies.Count);
        AssertExactDependency(dependencies, "Whisper.net", "1.9.1");
        AssertExactDependency(dependencies, "KokoroSharp", "0.8.4");
        AssertExactDependency(dependencies, "NAudio", "3.0.1");
        AssertExactDependency(dependencies, "Microsoft.ML.OnnxRuntime.Gpu", "1.29.0");

        foreach (var (platform, rid, extension) in new[] { ("Windows", "win-x64", ".dll"), ("Linux", "linux-x64", ".so") })
        {
            var id = "Oscova.Agents.Native.Whisper.Cuda." + platform;
            AssertExactDependency(dependencies, id, NativePackageVersion);
            using var payload = new PackageArchiveReader(GetPackagePath(id, NativePackageVersion));
            Assert.Equal(NuGetVersion.Parse(NativePackageVersion), payload.GetIdentity().Version);
            Assert.DoesNotContain(payload.GetPackageTypes(), type => type.Name == "OscovaPlugin");
            var payloadFiles = payload.GetFiles().Select(NormalizePath).ToArray();
            Assert.DoesNotContain(AgentPluginLoader.ManifestFileName, payloadFiles);
            Assert.DoesNotContain(payloadFiles, file => file.Contains("/lib/", StringComparison.Ordinal));
            var native = payloadFiles.Where(file => file.EndsWith(extension, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(native);
            Assert.All(native, file => Assert.StartsWith($"runtimes/{rid}/native/runtimes/cuda/{rid}/", file, StringComparison.Ordinal));
            Assert.Contains(native, file => file.Contains("ggml-cuda-whisper", StringComparison.Ordinal));
            Assert.Contains("licenses/whisper.cpp-LICENSE.txt", payloadFiles);
        }
    }

    private static string GetPackagePath(string id = PackageId, string version = PackageVersion)
    {
        var path = Path.Combine(
            Environment.GetEnvironmentVariable("OSCOVA_NUGET_PACKAGE_DIRECTORY")
                ?? Path.Combine(FindAssistantRoot(), "artifacts", "nuget"),
            $"{id}.{version}.nupkg");
        return File.Exists(path) ? path : throw new FileNotFoundException($"Release plugin package was not found: {path}.");
    }

    private static string FindAssistantRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Oscova.Assistant.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Oscova.Assistant repository root.");
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
