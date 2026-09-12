using Microsoft.Extensions.AI;
using System.Collections.Generic;

namespace Oscova.Agents.Providers;

/// <summary>Describes one recorded conversation between a caller and an agent it invoked as a tool.</summary>
public sealed record AgentToolConversation(
    string AgentId,
    string? Name,
    IReadOnlyList<ChatMessage> Messages);
