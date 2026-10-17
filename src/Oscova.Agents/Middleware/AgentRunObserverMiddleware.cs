using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Middleware;

/// <summary>
/// Adds an <see cref="IAgentRunObserver"/> intercept point to the chat client pipeline.
/// Must be placed before <see cref="FunctionInvokingChatClient"/> in the builder chain
/// so the observer fires once per model round-trip inside the tool loop, not once per
/// full agent run.
/// </summary>
public static class AgentRunObserverMiddleware
{
    /// <summary>Inserts a decorator that observes every request and each non-streaming response.</summary>
    public static ChatClientBuilder UseAgentRunObserver(
        this ChatClientBuilder builder,
        AgentRunObserverAccessor accessor)
    {
        return builder.Use(inner => new ObserverChatClient(inner, accessor));
    }

    private sealed class ObserverChatClient(IChatClient inner, AgentRunObserverAccessor accessor)
        : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = Snapshot(messages);
            if (accessor.Observer is { } observer) { await observer.OnRequestAsync(snapshot, options, cancellationToken).ConfigureAwait(false); }
            var response = await base.GetResponseAsync(snapshot, options, cancellationToken).ConfigureAwait(false);
            accessor.Observer?.OnResponse(response);
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var snapshot = Snapshot(messages);
            if (accessor.Observer is { } observer) { await observer.OnRequestAsync(snapshot, options, cancellationToken).ConfigureAwait(false); }
            await foreach (var update in base.GetStreamingResponseAsync(snapshot, options, cancellationToken).ConfigureAwait(false)) { yield return update; }
        }

        private static IReadOnlyList<ChatMessage> Snapshot(IEnumerable<ChatMessage> messages)
        {
            return messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        }
    }
}
