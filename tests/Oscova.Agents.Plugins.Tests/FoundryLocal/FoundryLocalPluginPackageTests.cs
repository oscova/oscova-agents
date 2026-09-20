using System.Text.Json;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Oscova.Agents.Plugins.Tests.FoundryLocal;

public sealed class FoundryLocalPluginPackageTests
{
    private const string PackageId = "Oscova.Agents.Plugins.FoundryLocal";
    private const string PackageVersion = "2.0.0";
    private const string PluginId = "oscova.foundry-local";
    private const long MaxPackageBytes = 26_214_400;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Package_ContainsReviewedManifestDependencyAndNotices()
    {
        var packagePath = GetPackagePath();
        using var archive = new PackageArchiveReader(packagePath);
        var files = archive.GetFiles().Select(NormalizePath).ToHashSet(StringComparer.Ordinal);
        using var nuspecStream = archive.GetNuspec();
        var dependencies = new NuspecReader(nuspecStream)
            .GetDependencyGroups()
            .SelectMany(group => group.Packages)
            .ToList();
        using var manifestStream = archive.GetStream(AgentPluginLoader.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<AgentPluginManifest>(manifestStream, JsonOptions);

        Assert.Contains(archive.GetPackageTypes(), packageType =>
            string.Equals(packageType.Name, "OscovaPlugin", StringComparison.Ordinal));
        Assert.NotNull(manifest);
        Assert.Equal(PluginId, manifest.Id);
        Assert.Equal(PackageVersion, manifest.Version);
        Assert.Equal("Oscova.Agents.Plugins.FoundryLocal.dll", manifest.Assembly);
        Assert.Contains("lib/net10.0/Oscova.Agents.Plugins.FoundryLocal.dll", files);

        var foundry = Assert.Single(dependencies);
        Assert.Equal("Microsoft.AI.Foundry.Local", foundry.Id);
        var foundryVersion = NuGetVersion.Parse("1.2.4");
        Assert.Equal(foundryVersion, foundry.VersionRange.MinVersion);
        Assert.Equal(foundryVersion, foundry.VersionRange.MaxVersion);
        Assert.True(foundry.VersionRange.IsMinInclusive);
        Assert.True(foundry.VersionRange.IsMaxInclusive);
        Assert.DoesNotContain(dependencies, dependency =>
            string.Equals(dependency.Id, "Microsoft.AI.Foundry.Local.WinML", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, dependency =>
            string.Equals(dependency.Id, "OpenAI", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, dependency =>
            string.Equals(dependency.Id, "Microsoft.Extensions.AI.OpenAI", StringComparison.Ordinal));

        Assert.DoesNotContain(files, path => path.StartsWith("runtimes/", StringComparison.Ordinal));
        Assert.Contains("README.md", files);
        Assert.Contains("licenses/Foundry-Local-LICENSE.txt", files);
        Assert.Contains("licenses/openai-dotnet-LICENSE.txt", files);
        Assert.Contains("licenses/dotnet-extensions-LICENSE.txt", files);
        Assert.Contains("licenses/dotnet-extensions-THIRD-PARTY-NOTICES.txt", files);
        Assert.True(new FileInfo(packagePath).Length <= MaxPackageBytes);
    }

    private static string GetPackagePath()
    {
        var packagePath = Path.Combine(
            FindAssistantRoot(),
            "artifacts",
            "nuget",
            $"{PackageId}.{PackageVersion}.nupkg");
        return File.Exists(packagePath)
            ? packagePath
            : throw new FileNotFoundException($"Release plugin package was not found: {packagePath}.");
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
