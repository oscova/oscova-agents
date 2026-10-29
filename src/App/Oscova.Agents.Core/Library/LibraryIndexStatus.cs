namespace Oscova.Agents.Core.Library;

/// <summary>Summarizes the current semantic-index state for an authorized Library.</summary>
public sealed record LibraryIndexStatus
{
    public LibraryIndexOverallState State { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> QueuedPaths { get; init; } = [];

    public IReadOnlyList<string> IndexingPaths { get; init; } = [];

    public IReadOnlyList<string> FailedPaths { get; init; } = [];

    public bool IsIncomplete =>
        State is LibraryIndexOverallState.Incomplete or LibraryIndexOverallState.Rebuilding ||
        QueuedPaths.Count > 0 ||
        IndexingPaths.Count > 0 ||
        FailedPaths.Count > 0;
}

/// <summary>Describes whether Library Search is ready, incomplete, rebuilding, or unavailable.</summary>
public enum LibraryIndexOverallState
{
    Unavailable,
    Ready,
    Incomplete,
    Rebuilding
}
