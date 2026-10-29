namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>Carries updated personal context content for a shared agent.</summary>
public sealed class SharedAgentPersonalContextSaveRequest
{
    public string? Content { get; set; }
}