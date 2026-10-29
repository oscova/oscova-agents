namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Tracks whether automatic compaction summarized earlier history during a run, so the reply can say so.
/// </summary>
public sealed class HistorySummarizedSignal
{
    private int _summarized;

    public bool Summarized => Volatile.Read(ref _summarized) == 1;

    /// <summary>
    /// Marks that earlier history was summarized for the current run.
    /// </summary>
    public void MarkSummarized() => Interlocked.Exchange(ref _summarized, 1);
}
