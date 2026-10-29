namespace Oscova.Agents.Core.Library;

/// <summary>Describes the durable indexing state of one Library file.</summary>
public enum LibraryFileIndexState
{
    Queued,
    Indexing,
    Ready,
    Skipped,
    Failed
}
