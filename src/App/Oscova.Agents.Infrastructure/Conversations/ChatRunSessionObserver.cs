using Oscova.Agents.Conversations;
using Microsoft.Extensions.AI;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Middleware;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>
/// Captures run metadata that is needed to inspect a persisted chat session later.
/// </summary>
public sealed class ChatRunSessionObserver(ChatThread thread, ChatSteeringTarget? steering = null,
    Action<IEnumerable<ChatMessage>>? captureRequest = null) : IAgentRunObserver
{
    public async Task OnRequestAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken = default)
    {
        captureRequest?.Invoke(messages);
        if (string.IsNullOrWhiteSpace(thread.SystemInstruction) &&
            !string.IsNullOrWhiteSpace(options?.Instructions))
        {
            thread.SystemInstruction = options.Instructions;
        }

        if (steering is not null) { await steering.MarkAppliedAsync(messages, cancellationToken).ConfigureAwait(false); }
    }

    public void OnResponse(ChatResponse response)
    {
    }
}
