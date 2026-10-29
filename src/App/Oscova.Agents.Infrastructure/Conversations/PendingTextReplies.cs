using System.Collections.Concurrent;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>Correlates one pending text reply per channel conversation.</summary>
internal sealed class PendingTextReplies<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, PendingReply> _waiters = new();

    public bool HasPending(TKey key) => _waiters.ContainsKey(key);

    public bool TryResolve(TKey key, string reply)
    {
        if (!_waiters.TryRemove(key, out var waiter))
        {
            return false;
        }

        waiter.Completion.TrySetResult(reply);
        return true;
    }

    public void Cancel(Func<TKey, bool> matches)
    {
        foreach (var pending in _waiters)
        {
            if (!matches(pending.Key) || !_waiters.TryRemove(pending)) { continue; }

            pending.Value.CancelRun?.Invoke();
            pending.Value.Completion.TrySetCanceled();
        }
    }

    public async Task<string?> SendAndAwaitAsync(
        TKey key,
        Func<CancellationToken, Task> sendPromptAsync,
        CancellationToken cancellationToken,
        Action? cancelRun = null)
    {
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingReply(waiter, cancelRun);
        _waiters[key] = pending;
        try
        {
            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            await sendPromptAsync(cancellationToken).ConfigureAwait(false);
            return await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _waiters.TryRemove(new KeyValuePair<TKey, PendingReply>(key, pending));
        }
    }

    /// <summary>Keeps the reply and its owning run's cancellation together until either wins.</summary>
    private sealed record PendingReply(TaskCompletionSource<string> Completion, Action? CancelRun);
}
