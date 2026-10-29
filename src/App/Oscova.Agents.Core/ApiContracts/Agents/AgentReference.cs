namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>A lightweight agent reference used in selection lists.</summary>
public sealed class AgentReference
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
