using System.Text.Json;
using NuGet.Packaging;
using NuGet.Versioning;
using Oscova.Agents.Tests.TestSupport;

namespace Oscova.Agents.Plugins.Tests.LlamaSharp;

public sealed class LlamaSharpPluginPackageTests
{
    private const string PackageId = "Oscova.Agents.Plugins.LlamaSharp";
    private static readonly string PackageVersion = PackageVersions.Get(PackageId);
    private const string PluginId = "oscova.llamasharp";
    private const long MaxPackageBytes = 26_214_400;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "Slow")]
    public void Package_ContainsReviewedManifestDependenciesAndRuntimeTrees()
    {
        var packagePath = ReleasePackages.GetPath(PackageId, PackageVersion);
        using var archive = new PackageArchiveReader(packagePath);
        var files = archive.GetFiles().Select(NormalizePath).ToHashSet(StringComparer.Ordinal);
        var packageTypes = archive.GetPackageTypes();
        using var nuspecStream = archive.GetNuspec();
        var dependencyGroups = new NuspecReader(nuspecStream).GetDependencyGroups().ToList();
        var dependencies = dependencyGroups.SelectMany(group => group.Packages).ToList();
        using var manifestStream = archive.GetStream(AgentPluginLoader.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<AgentPluginManifest>(manifestStream, JsonOptions);

        Assert.Contains(packageTypes, packageType =>
            string.Equals(packageType.Name, "OscovaPlugin", StringComparison.Ordinal));
        Assert.NotNull(manifest);
        Assert.Equal(PluginId, manifest.Id);
        Assert.Equal(PackageVersion, manifest.Version);
        Assert.Equal("Oscova.Agents.Plugins.LlamaSharp.dll", manifest.Assembly);
        Assert.Contains("lib/net10.0/Oscova.Agents.Plugins.LlamaSharp.dll", files);

        var llamaSharp = Assert.Single(dependencies, dependency =>
            string.Equals(dependency.Id, "LLamaSharp", StringComparison.Ordinal));
        var llamaSharpVersion = NuGetVersion.Parse("0.27.0");
        Assert.Equal(llamaSharpVersion, llamaSharp.VersionRange.MinVersion);
        Assert.Equal(llamaSharpVersion, llamaSharp.VersionRange.MaxVersion);
        Assert.True(llamaSharp.VersionRange.IsMinInclusive);
        Assert.True(llamaSharp.VersionRange.IsMaxInclusive);
        Assert.DoesNotContain(dependencies, dependency =>
            string.Equals(dependency.Id, "LLamaSharp.Backend.Cpu", StringComparison.Ordinal));

        Assert.Contains("licenses/LLamaSharp-LICENSE.txt", files);
        Assert.Contains("licenses/llama.cpp-LICENSE.txt", files);

        var runtimeRoots = files
            .Where(path => path.StartsWith("runtimes/", StringComparison.Ordinal))
            .Select(path => path.Split('/')[1])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["linux-x64", "osx-arm64", "osx-x64", "win-x64"], runtimeRoots);
        Assert.Contains("runtimes/win-x64/native/avx2/llama.dll", files);
        Assert.Contains("runtimes/win-x64/native/noavx/llama.dll", files);
        Assert.Contains("runtimes/linux-x64/native/avx2/libllama.so", files);
        Assert.Contains("runtimes/osx-arm64/native/libllama.dylib", files);
        Assert.Contains("runtimes/osx-x64/native/libllama.dylib", files);
        Assert.True(new FileInfo(packagePath).Length <= MaxPackageBytes);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
