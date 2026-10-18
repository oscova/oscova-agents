using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Vector;

/// <summary>Publishes replace and append vector generations without exposing incomplete records.</summary>
public sealed class DurableVectorIndex : IDisposable
{
    private const int SearchBatchSize = 50;
    private readonly string _vectorDatabasePath;
    private readonly string _collectionName;
    private readonly DurableVectorIndexManifestStore _manifestStore;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Func<VectorStoreService> _createStore;
    private readonly Func<CancellationToken, Task<IDisposable>> _acquireAccess;

    /// <summary>Creates an index over caller-owned database, manifest, and collection paths.</summary>
    public DurableVectorIndex(
        string vectorDatabasePath,
        string manifestPath,
        string collectionName,
        ILogger logger,
        Oscova.Agents.Secrets.IContentProtector protector,
        string userId,
        Func<VectorStoreService> createStore,
        Func<CancellationToken, Task<IDisposable>> acquireAccess)
    {
        if (string.IsNullOrWhiteSpace(vectorDatabasePath))
        {
            throw new ArgumentException("A vector database path is required.", nameof(vectorDatabasePath));
        }

        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("A manifest path is required.", nameof(manifestPath));
        }

        if (string.IsNullOrWhiteSpace(collectionName))
        {
            throw new ArgumentException("A collection name is required.", nameof(collectionName));
        }

        _vectorDatabasePath = vectorDatabasePath;
        _collectionName = collectionName;
        _createStore = createStore;
        _acquireAccess = acquireAccess;
        _manifestStore = new DurableVectorIndexManifestStore(
            manifestPath, logger, protector, userId, $"vector-manifest:{collectionName}:v1");
        _logger = logger;
    }

    /// <summary>Reads durable publication state without opening or creating the vector database.</summary>
    public async Task<DurableVectorIndexSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        DurableVectorIndexManifestLoadResult load =
            await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var sources = load.Manifest.Sources.ToDictionary(
            static pair => pair.Key,
            static pair => ToSnapshot(pair.Value),
            DurableVectorIndexManifest.SourceComparer);
        return new DurableVectorIndexSnapshot(
            load.Exists,
            load.IsTrusted && await HasBackingCollectionAsync(load, cancellationToken).ConfigureAwait(false),
            load.Manifest.Fingerprint,
            load.Manifest.VectorDimensions,
            sources);
    }

    /// <summary>Returns the committed append position for a source in the requested embedding space.</summary>
    public async Task<int> GetCommittedPositionAsync(
        DurableVectorSpace space,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        ValidateSpace(space);
        ValidateSourceId(sourceId);
        DurableVectorIndexManifestLoadResult load =
            await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(load, space) ||
            !await HasBackingCollectionAsync(load, cancellationToken).ConfigureAwait(false) ||
            !load.Manifest.Sources.TryGetValue(sourceId, out DurableVectorSourceEntry? entry) ||
            entry.Mode != DurableVectorSourceMode.Append)
        {
            return 0;
        }

        return entry.CommittedPosition;
    }

    /// <summary>Replaces one source with a fully committed vector generation.</summary>
    public async Task ReplaceAsync(
        DurableVectorSpace space,
        string sourceId,
        string generationId,
        IReadOnlyList<DurableVectorChunk> chunks,
        string? sourceMetadata = null,
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        ValidateSpace(space);
        ValidateSourceId(sourceId);
        ValidateGenerationId(generationId);
        if (chunks is null || chunks.Count == 0)
        {
            throw new ArgumentException("At least one vector chunk is required.", nameof(chunks));
        }

        ValidateChunks(chunks, space.VectorDimensions);

        bool publicationStarted = false;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableVectorIndexManifest manifest =
                await EnsureSpaceAsync(space, cancellationToken).ConfigureAwait(false);
            manifest.Sources[sourceId] = new DurableVectorSourceEntry
            {
                GenerationId = generationId,
                Metadata = sourceMetadata,
                Mode = DurableVectorSourceMode.Replace,
                State = DurableVectorSourceState.Pending
            };
            await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
            publicationStarted = true;
            await DeleteRecordsAsync(
                record => record.SourceId == sourceId,
                space.VectorDimensions,
                cancellationToken).ConfigureAwait(false);
            var records = CreateRecords(
                sourceId,
                ownerId: null,
                generationId,
                endPosition: 0,
                chunks);
            await PublishRecordsAsync(records, space.VectorDimensions, cancellationToken)
                .ConfigureAwait(false);

            manifest.Sources[sourceId] = new DurableVectorSourceEntry
            {
                GenerationId = generationId,
                Metadata = sourceMetadata,
                Mode = DurableVectorSourceMode.Replace,
                State = DurableVectorSourceState.Committed
            };
            await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch when (publicationStarted)
        {
            await TryCleanupSourceAsync(sourceId, space.VectorDimensions).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Appends one ordered batch when the source is still at the expected committed position.</summary>
    public async Task<DurableVectorAppendResult> AppendAsync(
        DurableVectorSpace space,
        string sourceId,
        string? ownerId,
        string generationId,
        int expectedPosition,
        int nextPosition,
        IReadOnlyList<DurableVectorChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        ValidateSpace(space);
        ValidateSourceId(sourceId);
        ValidateGenerationId(generationId);
        if (expectedPosition < 0 || nextPosition <= expectedPosition)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextPosition),
                "The next position must be greater than the non-negative expected position.");
        }

        if (chunks is null || chunks.Count == 0)
        {
            throw new ArgumentException("At least one vector chunk is required.", nameof(chunks));
        }

        ValidateChunks(chunks, space.VectorDimensions);

        bool publicationStarted = false;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableVectorIndexManifest manifest =
                await EnsureSpaceAsync(space, cancellationToken).ConfigureAwait(false);
            bool hasCurrent = manifest.Sources.TryGetValue(
                sourceId,
                out DurableVectorSourceEntry? current);
            int committedPosition = 0;
            if (hasCurrent && current!.Mode == DurableVectorSourceMode.Append)
            {
                committedPosition = current.CommittedPosition;
            }

            if (hasCurrent &&
                current!.Mode == DurableVectorSourceMode.Append &&
                committedPosition == nextPosition &&
                string.Equals(current.GenerationId, generationId, StringComparison.Ordinal))
            {
                return new DurableVectorAppendResult(committedPosition, false, false);
            }

            if ((hasCurrent && current!.Mode != DurableVectorSourceMode.Append) ||
                committedPosition != expectedPosition)
            {
                return new DurableVectorAppendResult(committedPosition, false, true);
            }

            manifest.Sources[sourceId] = new DurableVectorSourceEntry
            {
                GenerationId = generationId,
                OwnerId = ownerId,
                Mode = DurableVectorSourceMode.Append,
                State = DurableVectorSourceState.Pending,
                CommittedPosition = committedPosition
            };
            await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
            publicationStarted = true;
            await DeleteRecordsAsync(
                record => record.SourceId == sourceId && record.GenerationId == generationId,
                space.VectorDimensions,
                cancellationToken).ConfigureAwait(false);
            var records = CreateRecords(
                sourceId,
                ownerId,
                generationId,
                nextPosition,
                chunks);
            await PublishRecordsAsync(records, space.VectorDimensions, cancellationToken)
                .ConfigureAwait(false);

            manifest.Sources[sourceId] = new DurableVectorSourceEntry
            {
                GenerationId = generationId,
                OwnerId = ownerId,
                Mode = DurableVectorSourceMode.Append,
                State = DurableVectorSourceState.Committed,
                CommittedPosition = nextPosition
            };
            await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
            return new DurableVectorAppendResult(nextPosition, true, false);
        }
        catch when (publicationStarted)
        {
            await TryCleanupGenerationAsync(sourceId, generationId, space.VectorDimensions)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Marks a replacement source queued, skipped, or failed and removes its vectors.</summary>
    public async Task SetSourceStateAsync(
        DurableVectorSpace space,
        string sourceId,
        string generationId,
        DurableVectorSourceState state,
        string? reason = null,
        string? sourceMetadata = null,
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        ValidateSpace(space);
        ValidateSourceId(sourceId);
        if (state is not DurableVectorSourceState.Queued and
            not DurableVectorSourceState.Skipped and
            not DurableVectorSourceState.Failed)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableVectorIndexManifest manifest =
                await EnsureSpaceAsync(space, cancellationToken).ConfigureAwait(false);
            manifest.Sources[sourceId] = new DurableVectorSourceEntry
            {
                GenerationId = generationId,
                Metadata = sourceMetadata,
                Mode = DurableVectorSourceMode.Replace,
                State = state,
                Reason = reason
            };
            await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
            await DeleteRecordsAsync(
                record => record.SourceId == sourceId,
                space.VectorDimensions,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Searches only committed records from the current embedding space.</summary>
    public async Task<IReadOnlyList<DurableVectorHit>> SearchAsync(
        DurableVectorSpace space,
        Embedding<float> embedding,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        ValidateSpace(space);
        if (embedding is null || embedding.Vector.Length != space.VectorDimensions)
        {
            throw new ArgumentException("The query vector does not match the embedding space.", nameof(embedding));
        }

        if (maxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        }

        DurableVectorIndexManifestLoadResult load =
            await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(load, space) || !File.Exists(_vectorDatabasePath) ||
            !await HasBackingCollectionAsync(load, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using var store = _createStore();
        var results = new List<DurableVectorHit>(maxResults);
        for (int skip = 0; results.Count < maxResults; skip += SearchBatchSize)
        {
            var options = new VectorSearchOptions<DurableVectorRecord>
            {
                Filter = static record => record.IsCommitted,
                Skip = skip
            };
            IReadOnlyList<VectorSearchResult<DurableVectorRecord>> batch =
                await store.SearchAsync<string, DurableVectorRecord>(
                    _collectionName,
                    embedding,
                    SearchBatchSize,
                    options,
                    BuildCollectionDefinition(space.VectorDimensions)).ConfigureAwait(false);
            foreach (VectorSearchResult<DurableVectorRecord> result in batch)
            {
                if (IsVisible(result.Record, load.Manifest))
                {
                    results.Add(ToHit(result.Record, result.Score));
                    if (results.Count == maxResults)
                    {
                        break;
                    }
                }
            }

            if (batch.Count < SearchBatchSize)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>Reads committed records with optional owner and source filters.</summary>
    public async Task<IReadOnlyList<DurableVectorHit>> BrowseAsync(
        DurableVectorSpace space,
        string? ownerId,
        string? sourceId,
        int skip = 0,
        int take = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        ValidateSpace(space);
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip));
        }

        if (take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        IReadOnlyList<DurableVectorRecord> records = await ReadVisibleRecordsAsync(
            space,
            BuildBrowseFilter(ownerId, sourceId),
            skip,
            take,
            cancellationToken).ConfigureAwait(false);
        return [.. records
            .Select(static record => ToHit(record, score: null))];
    }

    /// <summary>Tombstones and removes one logical source without requiring a provider.</summary>
    public async Task<int> DeleteSourceAsync(
        string sourceId,
        int vectorDimensions,
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        ValidateSourceId(sourceId);
        ValidateDimensions(vectorDimensions);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableVectorIndexManifestLoadResult load =
                await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!load.IsTrusted) { return 0; }
            load.Manifest.Sources.Remove(sourceId);
            await _manifestStore.SaveAsync(load.Manifest, cancellationToken).ConfigureAwait(false);
            return await DeleteRecordsAsync(
                record => record.SourceId == sourceId,
                GetDimensions(load.Manifest, vectorDimensions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Tombstones and removes every logical source owned by one caller-defined owner.</summary>
    public async Task<int> DeleteByOwnerAsync(
        string ownerId,
        int vectorDimensions,
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException("An owner id is required.", nameof(ownerId));
        }

        ValidateDimensions(vectorDimensions);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableVectorIndexManifestLoadResult load =
                await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!load.IsTrusted) { return 0; }
            foreach (string sourceId in load.Manifest.Sources
                         .Where(pair => string.Equals(pair.Value.OwnerId, ownerId, StringComparison.Ordinal))
                         .Select(static pair => pair.Key)
                         .ToList())
            {
                load.Manifest.Sources.Remove(sourceId);
            }

            await _manifestStore.SaveAsync(load.Manifest, cancellationToken).ConfigureAwait(false);
            return await DeleteRecordsAsync(
                record => record.OwnerId == ownerId,
                GetDimensions(load.Manifest, vectorDimensions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Clears every source and its records while preserving the vector space and other collections.</summary>
    public async Task<int> DeleteAllSourcesAsync(int vectorDimensions, CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        ValidateDimensions(vectorDimensions);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableVectorIndexManifestLoadResult load =
                await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!load.IsTrusted) { return 0; }

            // Unpublish every source before deleting its stored records.
            load.Manifest.Sources.Clear();
            await _manifestStore.SaveAsync(load.Manifest, cancellationToken).ConfigureAwait(false);
            return await DeleteRecordsAsync(
                static _ => true,
                GetDimensions(load.Manifest, vectorDimensions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Publishes an empty trusted manifest before removing all physical records.</summary>
    public async Task ResetAsync(
        DurableVectorSpace space,
        CancellationToken cancellationToken = default)
    {
        using IDisposable access = await _acquireAccess(cancellationToken).ConfigureAwait(false);
        ValidateSpace(space);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ResetCoreAsync(space, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _mutationGate.Dispose();
    }

    /// <summary>Detects lost physical rows even when another collection recreated the database.</summary>
    private async Task<bool> HasBackingCollectionAsync(
        DurableVectorIndexManifestLoadResult load,
        CancellationToken cancellationToken)
    {
        bool expectsRecords = load.Manifest.Sources.Values.Any(static source =>
            source.State is DurableVectorSourceState.Pending or DurableVectorSourceState.Committed);
        if (!expectsRecords) { return true; }
        if (!File.Exists(_vectorDatabasePath)) { return false; }
        await using var store = _createStore();
        return await store.CollectionExistsAsync(_collectionName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DurableVectorIndexManifest> EnsureSpaceAsync(
        DurableVectorSpace space,
        CancellationToken cancellationToken)
    {
        DurableVectorIndexManifestLoadResult load =
            await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        bool hasBackingCollection = await HasBackingCollectionAsync(load, cancellationToken).ConfigureAwait(false);
        if (IsCurrent(load, space) && hasBackingCollection)
        {
            return load.Manifest;
        }

        if (!load.IsTrusted || !hasBackingCollection)
        {
            return await ResetCoreAsync(space, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "The durable vector index must be reset before changing its embedding space or rebuilding malformed state.");
    }

    private async Task<DurableVectorIndexManifest> ResetCoreAsync(
        DurableVectorSpace space,
        CancellationToken cancellationToken)
    {
        var manifest = new DurableVectorIndexManifest
        {
            Fingerprint = space.Fingerprint,
            VectorDimensions = space.VectorDimensions
        };
        // Publishing the empty manifest first keeps stale rows invisible if deletion is interrupted.
        await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
        if (File.Exists(_vectorDatabasePath))
        {
            await using var store = _createStore();
            await store.DeleteCollectionAsync<string, DurableVectorRecord>(
                _collectionName,
                BuildCollectionDefinition(space.VectorDimensions)).ConfigureAwait(false);
        }

        return manifest;
    }

    private async Task PublishRecordsAsync(
        List<DurableVectorRecord> records,
        int vectorDimensions,
        CancellationToken cancellationToken)
    {
        await using var store = _createStore();
        VectorStoreCollectionDefinition definition = BuildCollectionDefinition(vectorDimensions);
        await store.UpdateBatchAsync<string, DurableVectorRecord>(
            _collectionName,
            records,
            definition).ConfigureAwait(false);
        foreach (DurableVectorRecord record in records)
        {
            record.IsCommitted = true;
        }

        await store.UpdateBatchAsync<string, DurableVectorRecord>(
            _collectionName,
            records,
            definition).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<IReadOnlyList<DurableVectorRecord>> ReadVisibleRecordsAsync(
        DurableVectorSpace space,
        Expression<Func<DurableVectorRecord, bool>> filter,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        DurableVectorIndexManifestLoadResult load =
            await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(load, space) || !File.Exists(_vectorDatabasePath) ||
            !await HasBackingCollectionAsync(load, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using var store = _createStore();
        VectorStoreCollection<string, DurableVectorRecord> collection =
            await store.GetCollectionAsync<string, DurableVectorRecord>(
                _collectionName,
                BuildCollectionDefinition(space.VectorDimensions)).ConfigureAwait(false);
        var options = new FilteredRecordRetrievalOptions<DurableVectorRecord>
        {
            OrderBy = orderBy => orderBy
                .Descending(static record => record.Timestamp)
                .Descending(static record => record.ChunkIndex)
        };
        // Unpublished rows do not consume a visible page, and streaming stops after that page is filled.
        var visibleRecords = collection.GetAsync(filter, int.MaxValue, options, cancellationToken)
            .Where(record => IsVisible(record, load.Manifest));
        return await visibleRecords.Skip(skip).Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> DeleteRecordsAsync(
        Expression<Func<DurableVectorRecord, bool>> filter,
        int vectorDimensions,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_vectorDatabasePath))
        {
            return 0;
        }

        await using var store = _createStore();
        return await store.DeleteWhereAsync<string, DurableVectorRecord>(
            _collectionName,
            filter,
            static record => record.Key,
            BuildCollectionDefinition(vectorDimensions),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryCleanupSourceAsync(string sourceId, int vectorDimensions)
    {
        try
        {
            await DeleteRecordsAsync(
                record => record.SourceId == sourceId,
                vectorDimensions,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean incomplete vectors for source {SourceId}.", sourceId);
        }
    }

    private async Task TryCleanupGenerationAsync(
        string sourceId,
        string generationId,
        int vectorDimensions)
    {
        try
        {
            await DeleteRecordsAsync(
                record => record.SourceId == sourceId && record.GenerationId == generationId,
                vectorDimensions,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to clean incomplete vector generation {GenerationId} for source {SourceId}.",
                generationId,
                sourceId);
        }
    }

    private static List<DurableVectorRecord> CreateRecords(
        string sourceId,
        string? ownerId,
        string generationId,
        int endPosition,
        IReadOnlyList<DurableVectorChunk> chunks)
    {
        // Encode each identifier separately so embedded separators cannot collide across sources.
        string keyPrefix = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { sourceId, generationId })));
        var records = new List<DurableVectorRecord>(chunks.Count);
        foreach (DurableVectorChunk chunk in chunks)
        {
            records.Add(new DurableVectorRecord
            {
                Key = $"{keyPrefix}:{chunk.ChunkIndex}",
                SourceId = sourceId,
                OwnerId = ownerId,
                GenerationId = generationId,
                EndPosition = endPosition,
                IsCommitted = false,
                ChunkIndex = chunk.ChunkIndex,
                Metadata = chunk.Metadata,
                Timestamp = chunk.Timestamp ?? DateTimeOffset.UtcNow,
                Content = chunk.Content,
                Vector = chunk.Vector
            });
        }

        return records;
    }

    private static bool IsVisible(
        DurableVectorRecord record,
        DurableVectorIndexManifest manifest)
    {
        if (!record.IsCommitted ||
            !manifest.Sources.TryGetValue(record.SourceId, out DurableVectorSourceEntry? source))
        {
            return false;
        }

        return source.Mode == DurableVectorSourceMode.Replace
            ? source.State == DurableVectorSourceState.Committed &&
                string.Equals(source.GenerationId, record.GenerationId, StringComparison.Ordinal)
            : source.State is DurableVectorSourceState.Pending or DurableVectorSourceState.Committed &&
                record.EndPosition <= source.CommittedPosition;
    }

    private static Expression<Func<DurableVectorRecord, bool>> BuildBrowseFilter(
        string? ownerId,
        string? sourceId)
    {
        bool hasOwner = !string.IsNullOrWhiteSpace(ownerId);
        bool hasSource = !string.IsNullOrWhiteSpace(sourceId);
        if (hasOwner && hasSource)
        {
            return record => record.IsCommitted &&
                record.OwnerId == ownerId &&
                record.SourceId == sourceId;
        }

        if (hasOwner)
        {
            return record => record.IsCommitted && record.OwnerId == ownerId;
        }

        if (hasSource)
        {
            return record => record.IsCommitted && record.SourceId == sourceId;
        }

        return static record => record.IsCommitted;
    }

    private static DurableVectorSourceSnapshot ToSnapshot(DurableVectorSourceEntry entry) =>
        new(
            entry.GenerationId,
            entry.OwnerId,
            entry.Metadata,
            entry.Mode,
            entry.State,
            entry.CommittedPosition,
            entry.Reason);

    private static DurableVectorHit ToHit(DurableVectorRecord record, double? score) =>
        new(
            record.Key,
            record.SourceId,
            record.OwnerId,
            record.GenerationId,
            record.Content,
            record.ChunkIndex,
            record.Metadata,
            record.Timestamp,
            score);

    private static VectorStoreCollectionDefinition BuildCollectionDefinition(int dimensions) =>
        new()
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(DurableVectorRecord.Key), typeof(string)),
                new VectorStoreDataProperty(nameof(DurableVectorRecord.SourceId), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(DurableVectorRecord.OwnerId), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(DurableVectorRecord.GenerationId), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(DurableVectorRecord.EndPosition), typeof(int)),
                new VectorStoreDataProperty(nameof(DurableVectorRecord.IsCommitted), typeof(bool)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(DurableVectorRecord.ChunkIndex), typeof(int)),
                new VectorStoreDataProperty(nameof(DurableVectorRecord.Metadata), typeof(string)),
                new VectorStoreDataProperty(nameof(DurableVectorRecord.Timestamp), typeof(DateTimeOffset)),
                new VectorStoreDataProperty(nameof(DurableVectorRecord.Content), typeof(string)),
                new VectorStoreVectorProperty(
                    nameof(DurableVectorRecord.Vector),
                    typeof(ReadOnlyMemory<float>),
                    dimensions)
                {
                    DistanceFunction = DistanceFunction.CosineDistance
                }
            ]
        };

    private static bool IsCurrent(
        DurableVectorIndexManifestLoadResult load,
        DurableVectorSpace space) =>
        load.IsTrusted &&
        string.Equals(load.Manifest.Fingerprint, space.Fingerprint, StringComparison.Ordinal) &&
        load.Manifest.VectorDimensions == space.VectorDimensions;

    private static int GetDimensions(DurableVectorIndexManifest manifest, int fallback) =>
        manifest.VectorDimensions > 0 ? manifest.VectorDimensions : fallback;

    private static void ValidateSpace(DurableVectorSpace space)
    {
        if (space is null || string.IsNullOrWhiteSpace(space.Fingerprint))
        {
            throw new ArgumentException("An embedding-space fingerprint is required.", nameof(space));
        }

        ValidateDimensions(space.VectorDimensions);
    }

    private static void ValidateChunks(
        IReadOnlyList<DurableVectorChunk> chunks,
        int vectorDimensions)
    {
        var chunkIndices = new HashSet<int>();
        foreach (DurableVectorChunk? chunk in chunks)
        {
            if (chunk is null || string.IsNullOrWhiteSpace(chunk.Content) || chunk.ChunkIndex < 0)
            {
                throw new ArgumentException("Each vector chunk requires content and a non-negative index.", nameof(chunks));
            }

            if (chunk.Vector.Length != vectorDimensions)
            {
                throw new ArgumentException("A chunk vector does not match the embedding space.", nameof(chunks));
            }

            if (!chunkIndices.Add(chunk.ChunkIndex))
            {
                throw new ArgumentException("Vector chunk indices must be unique within a generation.", nameof(chunks));
            }
        }
    }

    private static void ValidateDimensions(int vectorDimensions)
    {
        if (vectorDimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vectorDimensions));
        }
    }

    private static void ValidateSourceId(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("A source id is required.", nameof(sourceId));
        }
    }

    private static void ValidateGenerationId(string generationId)
    {
        if (string.IsNullOrWhiteSpace(generationId))
        {
            throw new ArgumentException("A generation id is required.", nameof(generationId));
        }
    }
}
