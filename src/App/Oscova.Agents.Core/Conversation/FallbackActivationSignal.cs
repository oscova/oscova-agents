namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Tracks whether the fallback model provider served any response during a run.
/// </summary>
public sealed class FallbackActivationSignal
{
    private int _activated;

    public bool Activated => Volatile.Read(ref _activated) == 1;

    /// <summary>
    /// Marks fallback provider usage for the current run.
    /// </summary>
    public void MarkActivated() => Interlocked.Exchange(ref _activated, 1);
}
