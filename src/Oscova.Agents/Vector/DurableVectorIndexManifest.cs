using System;
using System.Collections.Generic;

namespace Oscova.Agents.Vector;

/// <summary>Tracks durable vector generations without persisting source content or host paths.</summary>
internal sealed class DurableVectorIndexManifest
{
    internal const int CurrentVersion = 1;

    public int Version { get; set; }

    internal static StringComparer SourceComparer { get; } = StringComparer.Ordinal;

    public string? Fingerprint { get; set; }

    public int VectorDimensions { get; set; }

    public Dictionary<string, DurableVectorSourceEntry> Sources { get; set; } =
        new(SourceComparer);
}

/// <summary>Persists safe publication metadata for one logical source.</summary>
internal sealed class DurableVectorSourceEntry
{
    public string GenerationId { get; set; } = string.Empty;

    public string? OwnerId { get; set; }

    public string? Metadata { get; set; }

    public DurableVectorSourceMode Mode { get; set; }

    public DurableVectorSourceState State { get; set; }

    public int CommittedPosition { get; set; }

    public string? Reason { get; set; }
}

/// <summary>Returns a manifest with enough trust information to prevent stale-vector search.</summary>
internal sealed record DurableVectorIndexManifestLoadResult(
    DurableVectorIndexManifest Manifest,
    bool Exists,
    bool IsMalformed)
{
    public bool IsTrusted => Exists && !IsMalformed && Manifest.Version == DurableVectorIndexManifest.CurrentVersion;
}
