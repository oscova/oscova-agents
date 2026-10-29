using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Connects governed agent calls to the current host-owned shared conversation.</summary>
public sealed class ChatConsultation
{
    public required Func<ChatClientAgent, string, bool,
        Func<ChatClientAgent, IEnumerable<ChatMessage>, AgentSession, CancellationToken, Task<AgentResponse>>,
        CancellationToken, Task<AgentResponse>> InvokeAsync { get; init; }

    public string? StreamingAgentId { get; set; }
    public Action<AgentResponseUpdate>? OnUpdate { get; set; }
}
