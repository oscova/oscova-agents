namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>Describes the custom agent created from an imported package.</summary>
public sealed class ImportedAgentResult
{
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int RemovedReferenceCount { get; set; }
}
