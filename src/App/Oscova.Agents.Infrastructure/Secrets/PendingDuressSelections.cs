using System.Collections.Concurrent;

namespace Oscova.Agents.Infrastructure.Secrets;

/// <summary>Retains opened duress selections until vault unlock in this Host process.</summary>
public sealed class PendingDuressSelections
{
    private readonly ConcurrentDictionary<string, DuressActions> _selections = new(StringComparer.Ordinal);

    /// <summary>Retains a selection opened by a locked-vault duress sign-in.</summary>
    public void Set(string userId, DuressActions actions) => _selections[userId] = actions;

    /// <summary>Returns whether application admission must wait for duress deletion.</summary>
    public bool Contains(string userId) => _selections.ContainsKey(userId);

    /// <summary>Returns the opened selection without consuming it before execution.</summary>
    public bool TryGet(string userId, out DuressActions actions) => _selections.TryGetValue(userId, out actions!);

    /// <summary>Clears a selection after its execution has run.</summary>
    public void Remove(string userId) => _selections.TryRemove(userId, out _);
}