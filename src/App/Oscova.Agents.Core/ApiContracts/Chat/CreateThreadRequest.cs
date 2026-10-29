namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Specifies the optional agent to assign when creating a new thread.</summary>
public sealed class CreateThreadRequest
{
    public string? AgentId { get; set; }
    public string? ProjectId { get; set; }
}
