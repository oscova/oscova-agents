using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Tests.Framework.Logging;
using Oscova.Agents.Vector;

namespace Oscova.Agents.Tests.Framework.Vector;

public sealed class DurableVectorIndexManifestStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "oscova-library-manifest-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoadAsync_PreservesSafeIndexState()
    {
        string path = Path.Combine(_root, "memory", "library-index.json");
        var store = new DurableVectorIndexManifestStore(
            path,
            NullLogger.Instance);
        var manifest = new DurableVectorIndexManifest
        {
            Fingerprint = "provider-space",
            VectorDimensions = 8,
            Sources =
            {
                ["notes/roadmap.md"] = new DurableVectorSourceEntry
                {
                    GenerationId = "ABC123",
                    State = DurableVectorSourceState.Committed
                }
            }
        };

        await store.SaveAsync(manifest);
        DurableVectorIndexManifestLoadResult loaded = await store.LoadAsync();

        Assert.True(loaded.IsTrusted);
        Assert.Equal(8, loaded.Manifest.VectorDimensions);
        KeyValuePair<string, DurableVectorSourceEntry> file = Assert.Single(loaded.Manifest.Sources);
        Assert.Equal("notes/roadmap.md", file.Key);
        Assert.Equal(DurableVectorSourceState.Committed, file.Value.State);
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ReturnsUntrustedEmptyManifest()
    {
        string path = Path.Combine(_root, "memory", "library-index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not-json");
        var store = new DurableVectorIndexManifestStore(
            path,
            NullLogger.Instance);

        DurableVectorIndexManifestLoadResult loaded = await store.LoadAsync();

        Assert.True(loaded.IsMalformed);
        Assert.False(loaded.IsTrusted);
        Assert.Empty(loaded.Manifest.Sources);
    }

    [Fact]
    public async Task LoadAsync_InvalidAppendState_ReturnsUntrustedEmptyManifest()
    {
        string path = Path.Combine(_root, "memory", "index.json");
        var store = new DurableVectorIndexManifestStore(path, NullLogger.Instance);
        var manifest = new DurableVectorIndexManifest
        {
            Fingerprint = "provider-space",
            VectorDimensions = 8,
            Sources =
            {
                ["thread"] = new DurableVectorSourceEntry
                {
                    GenerationId = "generation",
                    Mode = DurableVectorSourceMode.Append,
                    State = DurableVectorSourceState.Failed,
                    CommittedPosition = 1
                }
            }
        };
        await store.SaveAsync(manifest);

        DurableVectorIndexManifestLoadResult loaded = await store.LoadAsync();

        Assert.True(loaded.IsMalformed);
        Assert.False(loaded.IsTrusted);
        Assert.Empty(loaded.Manifest.Sources);
    }

    [Fact]
    public async Task LoadAsync_RepeatedMalformedEpisode_WarnsOnceUntilSuccessfulSave()
    {
        string path = Path.Combine(_root, "memory", "library-index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not-json");
        using var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var store = new DurableVectorIndexManifestStore(path, loggerFactory.CreateLogger("manifest"));

        await store.LoadAsync();
        await store.LoadAsync();

        Assert.Single(loggerProvider.Entries, entry => entry.Level == LogLevel.Warning);

        await store.SaveAsync(new DurableVectorIndexManifest
        {
            Fingerprint = "provider-space",
            VectorDimensions = 8
        });
        await File.WriteAllTextAsync(path, "{ malformed-again");
        await store.LoadAsync();

        Assert.Equal(2, loggerProvider.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
