using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Conversations;

/// <summary>Coordinates one active chat run per user thread across every channel.</summary>
public sealed class ThreadRunGuard
{
    private readonly ConcurrentDictionary<RunKey, RunEntry> _runs = new(RunKeyComparer.Instance);
    private readonly ConcurrentDictionary<string, UserRunState> _userStates = new(StringComparer.Ordinal);

    /// <summary>Gets the active run id for the thread without acquiring or releasing its guard.</summary>
    public string? GetActiveRunId(string userId, string threadId)
    {
        return _runs.TryGetValue(BuildKey(userId, threadId), out var entry)
            ? entry.RunId
            : null;
    }

    /// <summary>Reserves the thread's run slot, or returns null when another run holds it.</summary>
    public RunLease? Acquire(string userId, string threadId, string runId)
    {
        var userState = GetUserState(userId);
        lock (userState.SyncRoot)
        {
            if (userState.BlockReleased is not null)
            {
                return null;
            }

            var key = BuildKey(userId, threadId);
            var entry = new RunEntry(
                runId,
                new CancellationTokenSource(),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!_runs.TryAdd(key, entry))
            {
                entry.Cancellation.Dispose();
                return null;
            }

            if (userState.PendingRunIds.TryGetValue(threadId, out var pendingRunId) &&
                string.Equals(pendingRunId, runId, StringComparison.Ordinal))
            {
                userState.PendingRunIds.Remove(threadId);
                TryCancel(entry);
            }

            return new RunLease(this, key, entry);
        }
    }

    /// <summary>Waits for and reserves the thread's run slot.</summary>
    public async Task<RunLease> AcquireAsync(
        string userId,
        string threadId,
        string runId,
        CancellationToken ct = default)
    {
        var key = BuildKey(userId, threadId);
        var userState = GetUserState(userId);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var lease = Acquire(userId, threadId, runId);
            if (lease is not null)
            {
                return lease;
            }

            Task? userBlock;
            lock (userState.SyncRoot)
            {
                userBlock = userState.BlockReleased?.Task;
            }

            if (userBlock is not null)
            {
                await userBlock.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (_runs.TryGetValue(key, out var activeRun))
            {
                await activeRun.Released.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Cancels selected runs, drains every active run for one exact user, and blocks new admissions.</summary>
    public async Task<UserRunLease> CancelAndAcquireUserAsync(
        string userId,
        CancellationToken ct = default,
        IReadOnlyCollection<string>? threadIdsToCancel = null)
    {
        var userState = GetUserState(userId);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task? existingBlock;
            TaskCompletionSource? ownedBlock = null;
            KeyValuePair<RunKey, RunEntry>[] activeRuns = [];

            lock (userState.SyncRoot)
            {
                existingBlock = userState.BlockReleased?.Task;
                if (existingBlock is null)
                {
                    ownedBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    userState.BlockReleased = ownedBlock;
                    activeRuns = _runs
                        .Where(pair => string.Equals(pair.Key.UserId, userId, StringComparison.Ordinal))
                        .ToArray();
                }
            }

            if (ownedBlock is null)
            {
                await existingBlock!.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                foreach (var activeRun in activeRuns)
                {
                    if (threadIdsToCancel?.Contains(
                            activeRun.Key.ThreadId,
                            StringComparer.OrdinalIgnoreCase) is not false)
                    {
                        TryCancel(activeRun.Value);
                    }
                }

                await Task.WhenAll(activeRuns.Select(static pair => pair.Value.Released.Task))
                    .WaitAsync(ct)
                    .ConfigureAwait(false);
                return new UserRunLease(userState, ownedBlock);
            }
            catch
            {
                ReleaseUserBlock(userState, ownedBlock);
                throw;
            }
        }
    }

    /// <summary>Signals the active run for the thread to stop and returns its run id.</summary>
    public string? Cancel(string userId, string threadId)
    {
        if (!_runs.TryGetValue(BuildKey(userId, threadId), out var entry))
        {
            return null;
        }

        if (!TryCancel(entry))
        {
            // The run finished and released its lease between the lookup and the cancel.
            return null;
        }

        return entry.RunId;
    }

    /// <summary>Cancels only the matching active run without affecting a replacement run.</summary>
    public bool CancelExact(string userId, string threadId, string runId)
    {
        var userState = GetUserState(userId);
        lock (userState.SyncRoot)
        {
            return CancelExactLocked(userState, BuildKey(userId, threadId), runId, defer: false);
        }
    }

    /// <summary>Cancels the matching run or records that exact run for its pending admission.</summary>
    public void CancelOrDefer(string userId, string threadId, string runId)
    {
        var userState = GetUserState(userId);
        lock (userState.SyncRoot)
        {
            CancelExactLocked(userState, BuildKey(userId, threadId), runId, defer: true);
        }
    }

    private UserRunState GetUserState(string userId)
        => _userStates.GetOrAdd(userId, static _ => new UserRunState());

    private bool CancelExactLocked(
        UserRunState userState,
        RunKey key,
        string runId,
        bool defer)
    {
        if (_runs.TryGetValue(key, out var entry) &&
            string.Equals(entry.RunId, runId, StringComparison.Ordinal) &&
            TryCancel(entry))
        {
            return true;
        }

        if (defer)
        {
            userState.PendingRunIds[key.ThreadId] = runId;
        }

        return false;
    }

    private static bool TryCancel(RunEntry entry)
    {
        try
        {
            entry.Cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static void ReleaseUserBlock(UserRunState userState, TaskCompletionSource blockReleased)
    {
        lock (userState.SyncRoot)
        {
            if (!ReferenceEquals(userState.BlockReleased, blockReleased))
            {
                return;
            }

            userState.BlockReleased = null;
        }

        blockReleased.TrySetResult();
    }

    private static RunKey BuildKey(string userId, string threadId) => new(userId, threadId);

    internal readonly record struct RunKey(string UserId, string ThreadId);

    /// <summary>Keeps user identity exact while matching the repository's case-insensitive thread identity.</summary>
    private sealed class RunKeyComparer : IEqualityComparer<RunKey>
    {
        public static RunKeyComparer Instance { get; } = new();

        public bool Equals(RunKey left, RunKey right)
        {
            return string.Equals(left.UserId, right.UserId, StringComparison.Ordinal) &&
                   string.Equals(left.ThreadId, right.ThreadId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(RunKey key)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(key.UserId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.ThreadId));
        }
    }

    internal sealed record RunEntry(
        string RunId,
        CancellationTokenSource Cancellation,
        TaskCompletionSource Released);

    /// <summary>Tracks temporary user-wide admission blocking for authoritative bulk cleanup.</summary>
    internal sealed class UserRunState
    {
        public object SyncRoot { get; } = new();

        public TaskCompletionSource? BlockReleased { get; set; }

        public Dictionary<string, string> PendingRunIds { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Blocks new runs for one user until authoritative bulk cleanup finishes.</summary>
    public sealed class UserRunLease : IDisposable
    {
        private readonly UserRunState _userState;
        private readonly TaskCompletionSource _blockReleased;
        private int _disposed;

        internal UserRunLease(
            UserRunState userState,
            TaskCompletionSource blockReleased)
        {
            _userState = userState;
            _blockReleased = blockReleased;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            ReleaseUserBlock(_userState, _blockReleased);
        }
    }

    /// <summary>Owns a thread's run slot and stop token until disposed.</summary>
    public sealed class RunLease : IDisposable
    {
        private readonly ThreadRunGuard _owner;
        private readonly RunKey _key;
        private readonly RunEntry _entry;
        private int _disposed;

        internal RunLease(ThreadRunGuard owner, RunKey key, RunEntry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        /// <summary>Gets the token cancelled by a stop request for this run.</summary>
        public CancellationToken CancellationToken => _entry.Cancellation.Token;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner._runs.TryRemove(KeyValuePair.Create(_key, _entry));
            _entry.Released.TrySetResult();
            _entry.Cancellation.Dispose();
        }
    }
}
