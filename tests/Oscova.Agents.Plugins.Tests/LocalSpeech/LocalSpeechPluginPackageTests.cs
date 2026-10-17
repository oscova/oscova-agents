using System.Text.Json;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using Oscova.Agents.Tests.TestSupport;

namespace Oscova.Agents.Plugins.Tests.LocalSpeech;

/// <summary>Checks each speech capability's real CPU and CUDA publication graph.</summary>
public sealed class LocalSpeechPluginPackageTests
{
    private const string NativeVersion = "2.0.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("LocalSpeechRecognition", "oscova.local-speech-recognition", false)]
    [InlineData("LocalSpeechRecognition", "oscova.local-speech-recognition", true)]
    [InlineData("LocalSpeechSynthesis", "oscova.local-speech-synthesis", false)]
    [InlineData("LocalSpeechSynthesis", "oscova.local-speech-synthesis", true)]
    public void Package_ExportsMatchingCompatibilityAndIndependentDependencies(string capability, string pluginId, bool cuda)
    {
        var cpuId = "Oscova.Agents.Plugins." + capability;
        var packageId = cpuId + (cuda ? ".Cuda" : string.Empty);
        var path = ReleasePackages.GetPath(packageId, PackageVersions.Get(cpuId));
        using var archive = new PackageArchiveReader(path);
        var files = archive.GetFiles().Select(NormalizePath).ToHashSet(StringComparer.Ordinal);
        using var manifestStream = archive.GetStream(AgentPluginLoader.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<AgentPluginManifest>(manifestStream, JsonOptions)!;
        var dependencies = archive.GetPackageDependencies().SelectMany(group => group.Packages).ToArray();
        Assert.Contains(archive.GetPackageTypes(), type => type.Name == "OscovaPlugin");
        Assert.Equal(pluginId, manifest.Id);
        Assert.Equal(PackageVersions.Get(cpuId), manifest.Version);
        Assert.Equal(cpuId + ".dll", manifest.Assembly);
        var framework = Assert.Single(dependencies, dependency => dependency.Id == "Oscova.Agents");
        Assert.Equal(VersionRange.Parse(manifest.FrameworkVersionRange), framework.VersionRange);
        Assert.True(framework.VersionRange.Satisfies(NuGetVersion.Parse(PackageVersions.Get("Oscova.Agents"))));
        Assert.True(new FileInfo(path).Length <= 26_214_400);
        if (cuda)
        {
            Assert.DoesNotContain(files, file => file.StartsWith("lib/", StringComparison.Ordinal));
            Assert.Equal(new[] { $"runtimes/linux-x64/lib/net10.0/{cpuId}.dll", $"runtimes/win-x64/lib/net10.0/{cpuId}.dll" },
                files.Where(file => file.EndsWith(".dll", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        }
        else { Assert.Contains($"lib/net10.0/{cpuId}.dll", files); }
        if (capability == "LocalSpeechRecognition")
        {
            AssertExactDependency(dependencies, "Whisper.net", "1.9.1");
            AssertExactDependency(dependencies, "NAudio", "3.0.1");
            Assert.DoesNotContain(dependencies, dependency => dependency.Id == "KokoroSharp" || dependency.Id.StartsWith("Microsoft.ML.OnnxRuntime", StringComparison.Ordinal));
            Assert.DoesNotContain(dependencies, dependency => dependency.Id == "Whisper.net.Runtime");
            Assert.Contains("licenses/Whisper.net-LICENSE.txt", files);
            Assert.Contains("licenses/whisper.cpp-LICENSE.txt", files);
            if (cuda)
            {
                AssertExactDependency(dependencies, "Oscova.Agents.Native.Whisper.Cuda.Windows", NativeVersion);
                AssertExactDependency(dependencies, "Oscova.Agents.Native.Whisper.Cuda.Linux", NativeVersion);
            }
            else
            {
                Assert.Equal(new[] { "linux-x64", "osx-arm64", "osx-x64", "win-x64" },
                    files.Where(file => file.StartsWith("runtimes/", StringComparison.Ordinal)).Select(file => file.Split('/')[1]).Distinct().Order(StringComparer.Ordinal));
                foreach (var (rid, inner, library) in new[] { ("win-x64", "win-x64", "whisper.dll"), ("linux-x64", "linux-x64", "libwhisper.so"), ("osx-x64", "macos-x64", "libwhisper.dylib"), ("osx-arm64", "macos-arm64", "libwhisper.dylib") })
                {
                    Assert.Contains($"runtimes/{rid}/native/runtimes/{inner}/{library}", files);
                }
            }
        }
        else
        {
            AssertExactDependency(dependencies, "KokoroSharp", "0.8.4");
            AssertExactDependency(dependencies, cuda ? "Microsoft.ML.OnnxRuntime.Gpu" : "Microsoft.ML.OnnxRuntime", "1.29.0");
            Assert.DoesNotContain(dependencies, dependency => dependency.Id == "NAudio" || dependency.Id.Contains("Whisper", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("Windows", "win-x64", ".dll")]
    [InlineData("Linux", "linux-x64", ".so")]
    public void WhisperCudaPayload_IsAnOrdinaryNativeDependency(string platform, string rid, string extension)
    {
        using var archive = new PackageArchiveReader(ReleasePackages.GetPath("Oscova.Agents.Native.Whisper.Cuda." + platform, NativeVersion));
        var files = archive.GetFiles().Select(NormalizePath).ToArray();
        Assert.DoesNotContain(archive.GetPackageTypes(), type => type.Name == "OscovaPlugin");
        Assert.DoesNotContain(AgentPluginLoader.ManifestFileName, files);
        Assert.DoesNotContain(files, file => file.Contains("/lib/", StringComparison.Ordinal));
        var native = files.Where(file => file.EndsWith(extension, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(native);
        Assert.All(native, file => Assert.StartsWith($"runtimes/{rid}/native/runtimes/cuda/{rid}/", file, StringComparison.Ordinal));
        Assert.Contains(native, file => file.Contains("ggml-cuda-whisper", StringComparison.Ordinal));
        Assert.Contains("licenses/whisper.cpp-LICENSE.txt", files);
    }

    private static void AssertExactDependency(IReadOnlyList<PackageDependency> dependencies, string id, string version)
    {
        var dependency = Assert.Single(dependencies, item => item.Id == id);
        Assert.Equal(VersionRange.Parse($"[{version}]"), dependency.VersionRange);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
