using Oscova.Agents.Core.Conversation;

namespace Oscova.Agents.Core.Llm;

/// <summary>Observes Oscova requests by consumer and configured provider without reserving provider capacity.</summary>
public sealed class ModelProviderActivity
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string UserId, Guid ProviderId, RunExecutionKind Kind), int> _requests = [];

    /// <summary>Returns whether this consumer has a foreground request on this configured provider.</summary>
    public bool IsForegroundActive(string userId, Guid providerId)
    {
        lock (_gate)
        {
            return _requests.ContainsKey((userId, providerId, RunExecutionKind.WebChat)) ||
                _requests.ContainsKey((userId, providerId, RunExecutionKind.ChannelChat));
        }
    }

    /// <summary>Observes a request until its returned lease is disposed, including overlapping requests.</summary>
    public IDisposable Begin(string userId, Guid providerId, RunExecutionKind kind)
    {
        var key = (userId, providerId, kind);
        lock (_gate)
        {
            _requests.TryGetValue(key, out var count);
            _requests[key] = count + 1;
        }

        return new Request(this, key);
    }

    private sealed class Request(ModelProviderActivity owner,
        (string UserId, Guid ProviderId, RunExecutionKind Kind) key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }

            lock (owner._gate)
            {
                if (owner._requests[key] == 1) { owner._requests.Remove(key); }
                else { owner._requests[key]--; }
            }
        }
    }
}
