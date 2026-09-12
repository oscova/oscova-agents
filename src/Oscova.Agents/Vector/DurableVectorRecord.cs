using System;
using System.Collections.Generic;

namespace Oscova.Agents.Vector;

/// <summary>Identifies one embedding space used by a durable vector collection.</summary>
public sealed record DurableVectorSpace(string Fingerprint, int VectorDimensions);

/// <summary>Describes a precomputed chunk supplied to the durable vector index.</summary>
public sealed record DurableVectorChunk(
    string Content,
    ReadOnlyMemory<float> Vector,
    int ChunkIndex,
    string? Metadata = null,
    DateTimeOffset? Timestamp = null);

/// <summary>Identifies how one logical source publishes vector generations.</summary>
public enum DurableVectorSourceMode
{
    /// <summary>The source publishes one complete replacement generation.</summary>
    Replace,

    /// <summary>The source publishes ordered append batches.</summary>
    Append
}

/// <summary>Identifies the durable publication state of one logical source.</summary>
public enum DurableVectorSourceState
{
    /// <summary>The source is queued by its caller.</summary>
    Queued,

    /// <summary>The source has an incomplete generation.</summary>
    Pending,

    /// <summary>The source has committed searchable vectors.</summary>
    Committed,

    /// <summary>The caller intentionally skipped the source.</summary>
    Skipped,

    /// <summary>The source could not be indexed.</summary>
    Failed
}

/// <summary>Returns durable publication state for one logical source.</summary>
public sealed record DurableVectorSourceSnapshot(
    string GenerationId,
    string? OwnerId,
    string? Metadata,
    DurableVectorSourceMode Mode,
    DurableVectorSourceState State,
    int CommittedPosition,
    string? Reason);

/// <summary>Returns trusted durable-index state without exposing its persisted manifest shape.</summary>
public sealed record DurableVectorIndexSnapshot(
    bool Exists,
    bool IsTrusted,
    string? Fingerprint,
    int VectorDimensions,
    IReadOnlyDictionary<string, DurableVectorSourceSnapshot> Sources);

/// <summary>Reports whether an append committed, repeated an existing commit, or conflicted.</summary>
public sealed record DurableVectorAppendResult(
    int CommittedPosition,
    bool WasWritten,
    bool HasConflict);

/// <summary>Returns one committed vector search or browse hit.</summary>
public sealed record DurableVectorHit(
    string Key,
    string SourceId,
    string? OwnerId,
    string GenerationId,
    string Content,
    int ChunkIndex,
    string? Metadata,
    DateTimeOffset Timestamp,
    double? Score);

/// <summary>Stores one durable semantic chunk from a logical source generation.</summary>
internal sealed class DurableVectorRecord
{
    public string Key { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string? OwnerId { get; set; }

    public string GenerationId { get; set; } = string.Empty;

    public int EndPosition { get; set; }

    public bool IsCommitted { get; set; }

    public int ChunkIndex { get; set; }

    public string? Metadata { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public string Content { get; set; } = string.Empty;

    public ReadOnlyMemory<float> Vector { get; set; }
}
