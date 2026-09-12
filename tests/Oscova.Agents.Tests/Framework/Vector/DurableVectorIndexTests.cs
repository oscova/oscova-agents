using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;
using Oscova.Agents.Vector;

namespace Oscova.Agents.Tests.Framework.Vector;

public sealed class DurableVectorIndexTests : IDisposable
{
    private static readonly DurableVectorSpace Space = new("provider:model", 3);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "oscova-durable-vector-index-tests",
        Guid.NewGuid().ToString("N"));

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

        var store = new DurableVectorIndexManifestStore(ManifestPath, NullLogger.Instance);
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

        var store = new DurableVectorIndexManifestStore(ManifestPath, NullLogger.Instance);
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

        await File.WriteAllTextAsync(ManifestPath, "{ malformed");
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

        await using var store = new VectorStoreService(DatabasePath);
        VectorStoreCollection<string, SiblingRecord> sibling =
            await store.GetCollectionAsync<string, SiblingRecord>("sibling", SiblingDefinition());
        SiblingRecord remaining = Assert.Single(await sibling.GetAsync(record => true, 10).ToListAsync());
        Assert.Equal("keep", remaining.Key);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DatabasePath => Path.Combine(_root, "vectors.db");

    private string ManifestPath => Path.Combine(_root, "index.json");

    private DurableVectorIndex CreateIndex() =>
        new(DatabasePath, ManifestPath, "durable", NullLogger.Instance);

    private static DurableVectorChunk CreateChunk(string content, ReadOnlyMemory<float> vector) =>
        new(content, vector, 0, Metadata: "opaque", Timestamp: DateTimeOffset.UnixEpoch);

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
        await using var store = new VectorStoreService(DatabasePath);
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
