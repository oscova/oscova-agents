using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;
using System.Text.Json;
using System.Diagnostics;
using Oscova.Agents.Secrets;
using Oscova.Agents.Vector;

namespace Oscova.Agents.Tests.Framework.Vector;

public sealed class DurableVectorIndexTests : IDisposable
{
    private readonly Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector _protector = new();
    private sealed class NoopAccess : IDisposable { public void Dispose() { } }

    private static readonly DurableVectorSpace Space = new("provider:model", 3);
    private readonly string _root = Environment.GetEnvironmentVariable("OSCOVA_VECTOR_RECOVERY_TEST_ROOT") ?? Path.Combine(
        Path.GetTempPath(),
        "oscova-durable-vector-index-tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>Distinct source and generation pairs never overwrite each other's chunks.</summary>
    [Fact]
    public async Task ReplaceAsync_SeparatorBearingIdentifiers_PreserveBothSources()
    {
        using var index = CreateIndex();
        await index.ReplaceAsync(Space, "a\nb", "c", [CreateChunk("first", new float[] { 1, 0, 0 })]);
        await index.ReplaceAsync(Space, "a", "b\nc", [CreateChunk("second", new float[] { 0, 1, 0 })]);

        Assert.Equal("first", Assert.Single(await index.BrowseAsync(Space, null, "a\nb")).Content);
        Assert.Equal("second", Assert.Single(await index.BrowseAsync(Space, null, "a")).Content);
    }

    /// <summary>Unpublished rows do not consume the requested page of visible records.</summary>
    [Theory]
    [InlineData(0, "source-1")]
    [InlineData(1, "source-0")]
    public async Task BrowseAsync_DuringPublication_PagesVisibleRecords(int skip, string expectedSource)
    {
        var publicationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePublication = new ManualResetEventSlim();
        var protector = new CallbackProtector(_protector, plaintext =>
        {
            using var json = JsonDocument.Parse(plaintext);
            if (json.RootElement.GetProperty("sources").TryGetProperty("publishing", out var source) &&
                source.GetProperty("state").GetInt32() == (int)DurableVectorSourceState.Committed)
            {
                publicationReached.TrySetResult();
                if (!releasePublication.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException(); }
            }
        });
        using var index = CreateIndex(protector);
        for (int i = 0; i < 2; i++)
        {
            await index.ReplaceAsync(Space, $"source-{i}", "generation",
                [new DurableVectorChunk($"content-{i}", new float[] { 1, 0, 0 }, 0,
                    Timestamp: DateTimeOffset.UnixEpoch.AddMinutes(i))]);
        }

        // Pause the final manifest publication after the real database rows have been written.
        var publication = Task.Run(() => index.ReplaceAsync(Space, "publishing", "generation",
            [new DurableVectorChunk("pending", new float[] { 1, 0, 0 }, 0,
                Timestamp: DateTimeOffset.UnixEpoch.AddMinutes(2))]));
        try
        {
            await publicationReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var page = await index.BrowseAsync(Space, null, null, skip, take: 1);
            Assert.Equal(expectedSource, Assert.Single(page).SourceId);
        }
        finally
        {
            releasePublication.Set();
            await publication;
        }
    }

    [Fact]
    public async Task AppendAsync_IsIdempotent_AndRejectsOverlappingRange()
    {
        using DurableVectorIndex index = CreateIndex();
        DurableVectorChunk chunk = CreateChunk("first", new float[] { 1, 0, 0 });

        DurableVectorAppendResult first = await index.AppendAsync(
            Space, "thread", "agent", "0:2:hash", 0, 2, [chunk]);
        DurableVectorAppendResult retry = await index.AppendAsync(
            Space, "thread", "agent", "0:2:hash", 0, 2, [chunk]);
        DurableVectorAppendResult overlap = await index.AppendAsync(
            Space, "thread", "agent", "1:3:other", 1, 3, [CreateChunk("other", new float[] { 0, 1, 0 })]);

        IReadOnlyList<DurableVectorHit> records = await index.BrowseAsync(Space, null, "thread");
        Assert.True(first.WasWritten);
        Assert.False(retry.WasWritten);
        Assert.False(retry.HasConflict);
        Assert.True(overlap.HasConflict);
        Assert.Equal(2, overlap.CommittedPosition);
        Assert.Single(records);
        Assert.Equal("first", records[0].Content);
    }

    /// <summary>A process interruption cannot publish abandoned excerpts beside a larger replacement batch.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task AppendAsync_ProcessInterruptedBeforeManifestCommit_ReplaysWithoutDuplicates()
    {
        var marker = Path.Combine(_root, "publication-reached");
        if (Environment.GetEnvironmentVariable("OSCOVA_VECTOR_RECOVERY_TEST_ROOT") is not null)
        {
            var protector = new CallbackProtector(_protector, plaintext =>
            {
                using var json = JsonDocument.Parse(plaintext);
                if (json.RootElement.GetProperty("sources").TryGetProperty("thread", out var source) &&
                    source.GetProperty("committedPosition").GetInt32() == 2)
                {
                    // Stop this disposable test process after actual vector writes, before manifest publication.
                    File.WriteAllText(marker, "rows written");
                    Process.GetCurrentProcess().Kill();
                }
            });
            using var interrupted = CreateIndex(protector);
            await interrupted.AppendAsync(Space, "thread", "agent", "interrupted", 1, 2,
                [CreateChunk("interrupted excerpt", new float[] { 1, 0, 0 })]);
            return;
        }

        using (var initial = CreateIndex())
        {
            await initial.AppendAsync(Space, "thread", "agent", "earlier", 0, 1,
                [CreateChunk("earlier committed excerpt", new float[] { 1, 0, 0 })]);
        }

        await RunInterruptedChildAsync(nameof(AppendAsync_ProcessInterruptedBeforeManifestCommit_ReplaysWithoutDuplicates), marker);
        using var recovered = CreateIndex();
        Assert.Equal("earlier committed excerpt", Assert.Single(await recovered.BrowseAsync(Space, null, "thread")).Content);
        await recovered.AppendAsync(Space, "thread", "agent", "replacement", 1, 3,
            [CreateChunk("replacement excerpt", new float[] { 1, 0, 0 })]);

        var records = await recovered.BrowseAsync(Space, null, "thread");
        Assert.Equal(2, records.Count);
        Assert.Contains(records, record => record.Content == "earlier committed excerpt");
        Assert.Contains(records, record => record.Content == "replacement excerpt");
        Assert.DoesNotContain(records, record => record.Content == "interrupted excerpt");
    }

    /// <summary>Replays retained sources after an interrupted embedding-dimension reset.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ResetAsync_InterruptedBeforeCollectionDeletion_ReplaysInNewSpace()
    {
        var replacementSpace = new DurableVectorSpace("provider:replacement", 4);
        var marker = Path.Combine(_root, "collection-deletion-reached");
        if (Environment.GetEnvironmentVariable("OSCOVA_VECTOR_RECOVERY_TEST_ROOT") is not null)
        {
            using var interrupted = CreateIndex(createStore: () =>
            {
                File.WriteAllText(marker, "collection deletion reached");
                Process.GetCurrentProcess().Kill();
                throw new InvalidOperationException("The disposable test process did not terminate.");
            });
            await interrupted.ResetAsync(replacementSpace);
            return;
        }

        using (var initial = CreateIndex())
        {
            await initial.AppendAsync(Space, "thread", "agent", "old", 0, 1,
                [CreateChunk("old excerpt", new float[] { 1, 0, 0 })]);
        }
        await SeedSiblingCollectionAsync();
        await RunInterruptedChildAsync(nameof(ResetAsync_InterruptedBeforeCollectionDeletion_ReplaysInNewSpace), marker);

        using var recovered = CreateIndex();
        var snapshot = await recovered.GetSnapshotAsync();
        Assert.False(snapshot.IsTrusted);

        await recovered.AppendAsync(replacementSpace, "thread", "agent", "new", 0, 1,
            [CreateChunk("replayed excerpt", new float[] { 1, 0, 0, 0 })]);
        Assert.Equal("replayed excerpt", Assert.Single(await recovered.BrowseAsync(replacementSpace, null, "thread")).Content);
        await using var store = CreateStore();
        var sibling = await store.GetCollectionAsync<string, SiblingRecord>("sibling", SiblingDefinition());
        Assert.Equal("keep", Assert.Single(await sibling.GetAsync(record => true, 10).ToListAsync()).Key);
    }

    [Fact]
    public async Task AppendAsync_ConcurrentIdenticalBatch_CommitsOnce()
    {
        using DurableVectorIndex index = CreateIndex();
        using var barrier = new Barrier(2);

        Task<DurableVectorAppendResult> first = Task.Run(() => AppendAfterBarrierAsync(index, barrier));
        Task<DurableVectorAppendResult> second = Task.Run(() => AppendAfterBarrierAsync(index, barrier));
        DurableVectorAppendResult[] results = await Task.WhenAll(first, second);

        Assert.Single(results, static result => result.WasWritten);
        Assert.Single(results, static result => !result.WasWritten && !result.HasConflict);
        Assert.Single(await index.BrowseAsync(Space, null, "thread"));
    }

    [Fact]
    public async Task AppendAsync_PendingPublicationIsHidden_AndRetryRecovers()
    {
        using DurableVectorIndex index = CreateIndex();
        DurableVectorChunk chunk = CreateChunk("recover", new float[] { 1, 0, 0 });
        await index.AppendAsync(Space, "thread", "agent", "0:1:hash", 0, 1, [chunk]);

        var store = new DurableVectorIndexManifestStore(ManifestPath, NullLogger.Instance, _protector, "vector-test", "vector-manifest:durable:v1");
        DurableVectorIndexManifest manifest = (await store.LoadAsync()).Manifest;
        manifest.Sources["thread"].State = DurableVectorSourceState.Pending;
        manifest.Sources["thread"].CommittedPosition = 0;
        await store.SaveAsync(manifest);

        Assert.Empty(await index.BrowseAsync(Space, null, "thread"));

        DurableVectorAppendResult recovered = await index.AppendAsync(
            Space, "thread", "agent", "0:1:hash", 0, 1, [chunk]);

        Assert.True(recovered.WasWritten);
        Assert.Single(await index.BrowseAsync(Space, null, "thread"));
    }

    [Fact]
    public async Task ReplaceAsync_InvalidGenerationPreservesCommittedSource()
    {
        using DurableVectorIndex index = CreateIndex();
        await index.ReplaceAsync(Space, "source", "old", [CreateChunk("old", new float[] { 1, 0, 0 })]);

        await Assert.ThrowsAnyAsync<Exception>(() => index.ReplaceAsync(
            Space,
            "source",
            "new",
            [CreateChunk("invalid", new float[] { 1, 0 })]));

        DurableVectorIndexSnapshot snapshot = await index.GetSnapshotAsync();
        Assert.Equal(DurableVectorSourceState.Committed, snapshot.Sources["source"].State);
        DurableVectorHit committed = Assert.Single(await index.BrowseAsync(Space, null, "source"));
        Assert.Equal("old", committed.Content);
    }

    [Fact]
    public async Task AppendAsync_FingerprintMismatchRequiresExplicitReset()
    {
        using DurableVectorIndex index = CreateIndex();
        await index.AppendAsync(
            Space,
            "thread",
            "agent",
            "original",
            0,
            1,
            [CreateChunk("original", new float[] { 1, 0, 0 })]);
        var changedSpace = new DurableVectorSpace("different-provider", 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() => index.AppendAsync(
            changedSpace,
            "thread",
            "agent",
            "replacement",
            0,
            1,
            [CreateChunk("replacement", new float[] { 0, 1, 0 })]));

        DurableVectorHit committed = Assert.Single(await index.BrowseAsync(Space, null, "thread"));
        Assert.Equal("original", committed.Content);
    }

    [Fact]
    public async Task SearchAsync_ContinuesPastStaleHigherRankedRows()
    {
        using DurableVectorIndex index = CreateIndex();
        for (int i = 0; i < 50; i++)
        {
            await index.ReplaceAsync(
                Space,
                $"stale-{i}",
                "generation",
                [CreateChunk($"stale-{i}", new float[] { 1, 0, 0 })]);
        }

        await index.ReplaceAsync(
            Space,
            "valid",
            "generation",
            [CreateChunk("valid", new float[] { 0.9f, 0.1f, 0 })]);

        var store = new DurableVectorIndexManifestStore(ManifestPath, NullLogger.Instance, _protector, "vector-test", "vector-manifest:durable:v1");
        DurableVectorIndexManifest manifest = (await store.LoadAsync()).Manifest;
        foreach (DurableVectorSourceEntry source in manifest.Sources.Values)
        {
            if (source.GenerationId == "generation")
            {
                source.State = DurableVectorSourceState.Pending;
            }
        }

        manifest.Sources["valid"].State = DurableVectorSourceState.Committed;
        await store.SaveAsync(manifest);

        IReadOnlyList<DurableVectorHit> results = await index.SearchAsync(
            Space,
            new Embedding<float>(new float[] { 1, 0, 0 }),
            1);

        DurableVectorHit result = Assert.Single(results);
        Assert.Equal("valid", result.SourceId);
    }

    [Fact]
    public async Task BrowseAsync_RejectsMalformedAndMismatchedManifest()
    {
        using DurableVectorIndex index = CreateIndex();
        await index.ReplaceAsync(Space, "source", "generation", [CreateChunk("content", new float[] { 1, 0, 0 })]);

        Assert.Empty(await index.BrowseAsync(new DurableVectorSpace("other", 3), null, null));

        await File.WriteAllTextAsync(ManifestPath, _protector.Protect("vector-test", "vector-manifest:durable:v1", "{ malformed"));
        Assert.Empty(await index.BrowseAsync(Space, null, null));
    }

    [Fact]
    public async Task BrowseAsync_WithPaging_ReturnsOrderedSlice()
    {
        using DurableVectorIndex index = CreateIndex();
        for (int i = 0; i < 4; i++)
        {
            await index.ReplaceAsync(
                Space,
                $"source-{i}",
                $"generation-{i}",
                [new DurableVectorChunk(
                    $"content-{i}",
                    new float[] { 1, 0, 0 },
                    0,
                    Timestamp: DateTimeOffset.UnixEpoch.AddMinutes(i))]);
        }

        IReadOnlyList<DurableVectorHit> page = await index.BrowseAsync(
            Space,
            ownerId: null,
            sourceId: null,
            skip: 1,
            take: 2);

        Assert.Equal(["source-2", "source-1"], page.Select(static hit => hit.SourceId));
    }

    [Fact]
    public async Task ResetAndDeletion_TombstoneTargets_AndPreserveSiblingCollection()
    {
        using DurableVectorIndex index = CreateIndex();
        await index.AppendAsync(
            Space,
            "thread-a",
            "agent-a",
            "a",
            0,
            1,
            [CreateChunk("a", new float[] { 1, 0, 0 })]);
        await index.AppendAsync(
            Space,
            "thread-b",
            "agent-b",
            "b",
            0,
            1,
            [CreateChunk("b", new float[] { 0, 1, 0 })]);
        await SeedSiblingCollectionAsync();

        Assert.Equal(1, await index.DeleteSourceAsync("thread-a", 3));
        Assert.Equal(1, await index.DeleteByOwnerAsync("agent-b", 3));
        Assert.Empty((await index.GetSnapshotAsync()).Sources);

        await index.ResetAsync(Space);

        await using var store = new VectorStoreService(DatabasePath, new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        VectorStoreCollection<string, SiblingRecord> sibling =
            await store.GetCollectionAsync<string, SiblingRecord>("sibling", SiblingDefinition());
        SiblingRecord remaining = Assert.Single(await sibling.GetAsync(record => true, 10).ToListAsync());
        Assert.Equal("keep", remaining.Key);
    }

    [Fact]
    public async Task DeleteAllSourcesAsync_ClearsRecordsAndPreservesSiblingCollection()
    {
        using DurableVectorIndex index = CreateIndex();
        await index.AppendAsync(Space, "thread-a", "agent-a", "a", 0, 1,
            [CreateChunk("a", new float[] { 1, 0, 0 })]);
        await index.ReplaceAsync(Space, "source-b", "b",
            [CreateChunk("b", new float[] { 0, 1, 0 })]);
        await SeedSiblingCollectionAsync();

        Assert.Equal(2, await index.DeleteAllSourcesAsync(3));
        DurableVectorIndexSnapshot snapshot = await index.GetSnapshotAsync();
        Assert.Empty(snapshot.Sources);
        Assert.Equal(Space.Fingerprint, snapshot.Fingerprint);
        Assert.Empty(await index.BrowseAsync(Space, null, null));

        await using var store = new VectorStoreService(DatabasePath,
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        var sibling = await store.GetCollectionAsync<string, SiblingRecord>("sibling", SiblingDefinition());
        Assert.Equal("keep", Assert.Single(await sibling.GetAsync(record => true, 10).ToListAsync()).Key);

        await index.AppendAsync(Space, "thread-a", "agent-a", "a", 0, 1,
            [CreateChunk("new", new float[] { 1, 0, 0 })]);
        Assert.Equal("new", Assert.Single(await index.BrowseAsync(Space, null, null)).Content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DatabasePath => Path.Combine(_root, "vectors.db");

    private string ManifestPath => Path.Combine(_root, "index.json.enc");

    private DurableVectorIndex CreateIndex(IContentProtector? protector = null, Func<VectorStoreService>? createStore = null) =>
        new(DatabasePath, ManifestPath, "durable", NullLogger.Instance, protector ?? _protector, "vector-test",
            createStore ?? CreateStore, _ => Task.FromResult<IDisposable>(new NoopAccess()));

    private VectorStoreService CreateStore() => new(DatabasePath,
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());

    private async Task RunInterruptedChildAsync(string testName, string marker)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(DurableVectorIndexTests).Assembly.Location);
        start.ArgumentList.Add($"--TestCaseFilter:FullyQualifiedName={typeof(DurableVectorIndexTests).FullName}.{testName}");
        start.Environment["OSCOVA_VECTOR_RECOVERY_TEST_ROOT"] = _root;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }

        Assert.True(File.Exists(marker), (await stdout) + (await stderr));
        Assert.NotEqual(0, child.ExitCode);
    }

    private static DurableVectorChunk CreateChunk(string content, ReadOnlyMemory<float> vector) =>
        new(content, vector, 0, Metadata: "opaque", Timestamp: DateTimeOffset.UnixEpoch);

    private sealed class CallbackProtector(IContentProtector inner, Action<string> beforeProtect) : IContentProtector
    {
        public bool IsAvailable(string userId) => inner.IsAvailable(userId);
        public string? Unprotect(string userId, string purpose, string ciphertext) => inner.Unprotect(userId, purpose, ciphertext);
        public string Protect(string userId, string purpose, string plaintext)
        {
            beforeProtect(plaintext);
            return inner.Protect(userId, purpose, plaintext);
        }
    }

    private static async Task<DurableVectorAppendResult> AppendAfterBarrierAsync(
        DurableVectorIndex index,
        Barrier barrier)
    {
        barrier.SignalAndWait();
        return await index.AppendAsync(
            Space,
            "thread",
            "agent",
            "0:1:hash",
            0,
            1,
            [CreateChunk("content", new float[] { 1, 0, 0 })]);
    }

    private async Task SeedSiblingCollectionAsync()
    {
        await using var store = new VectorStoreService(DatabasePath, new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        await store.UpdateBatchAsync<string, SiblingRecord>(
            "sibling",
            [new SiblingRecord { Key = "keep", Vector = new float[] { 1, 0, 0 } }],
            SiblingDefinition());
    }

    private static VectorStoreCollectionDefinition SiblingDefinition() =>
        new()
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(SiblingRecord.Key), typeof(string)),
                new VectorStoreVectorProperty(
                    nameof(SiblingRecord.Vector),
                    typeof(ReadOnlyMemory<float>),
                    dimensions: 3)
            ]
        };

    private sealed class SiblingRecord
    {
        public string Key { get; set; } = string.Empty;

        public ReadOnlyMemory<float> Vector { get; set; }
    }
}
