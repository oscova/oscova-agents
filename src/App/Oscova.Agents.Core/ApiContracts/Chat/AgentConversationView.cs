using Microsoft.Extensions.AI;
using Oscova.Agents.Providers;

namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Carries one delegated agent conversation for the read-only chat view.</summary>
public sealed class AgentConversationView
{
    /// <summary>Gets or sets the stable ID of the agent that was called.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Gets or sets the caller-supplied conversation name, when one was given.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the subagent handle that identifies this conversation in the thread.</summary>
    public string Handle { get; set; } = string.Empty;

    /// <summary>Gets or sets the last observed subagent state.</summary>
    public DelegatedWorkerStatus Status { get; set; }

    /// <summary>Gets or sets the child agent's recorded turns.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; set; } = [];
}
