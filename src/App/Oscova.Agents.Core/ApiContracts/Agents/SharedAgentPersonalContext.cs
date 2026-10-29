namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>The current user's personal context document for a shared agent.</summary>
public sealed class SharedAgentPersonalContext
{
    public string AgentId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}