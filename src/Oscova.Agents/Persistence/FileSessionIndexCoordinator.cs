using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Persistence;

/// <summary>Serializes process-local updates to session index files that are rewritten atomically.</summary>
public sealed class FileSessionIndexCoordinator
{
    private readonly Lock _entriesLock = new();
    private readonly Dictionary<string, LockEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs an update while excluding concurrent updates for the same user.</summary>
    public Task RunForUserAsync(
        string userId,
        Func<CancellationToken, Task> updateAsync,
        CancellationToken ct = default)
    {
        return RunAsync($"chat:{userId}", updateAsync, ct);
    }

    /// <summary>Runs an update while excluding concurrent updates for the same task.</summary>
    public Task RunForTaskAsync(
        string userId,
        string taskId,
        Func<CancellationToken, Task> updateAsync,
        CancellationToken ct = default)
    {
        // The user-id length makes the component boundary unambiguous when either id contains ':'.
        return RunAsync($"task:{userId.Length}:{userId}:{taskId}", updateAsync, ct);
    }

    private async Task RunAsync(
        string key,
        Func<CancellationToken, Task> updateAsync,
        CancellationToken ct)
    {
        var entry = AcquireEntry(key);
        var entered = false;
        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            await updateAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                entry.Gate.Release();
            }

            ReleaseEntry(key, entry);
        }
    }

    private LockEntry AcquireEntry(string key)
    {
        lock (_entriesLock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new LockEntry();
                _entries.Add(key, entry);
            }

            entry.ReferenceCount++;
            return entry;
        }
    }

    private void ReleaseEntry(string key, LockEntry entry)
    {
        lock (_entriesLock)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                _entries.TryGetValue(key, out var current) &&
                ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class LockEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }
}
