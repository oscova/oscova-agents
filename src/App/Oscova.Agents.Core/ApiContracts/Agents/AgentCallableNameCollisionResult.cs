namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>Identifies an agent save rejected because its callable name is already assigned.</summary>
public sealed class AgentCallableNameCollisionResult
{
    /// <summary>Gets or sets the rejected agent display name.</summary>
    public string AgentName { get; set; } = string.Empty;

    /// <summary>Gets or sets the normalized callable name that collided.</summary>
    public string CallableName { get; set; } = string.Empty;
}
