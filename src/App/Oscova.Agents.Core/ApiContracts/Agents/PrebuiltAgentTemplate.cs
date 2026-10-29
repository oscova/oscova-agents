namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>Describes a plugin starter that can become an editable custom agent.</summary>
public sealed class PrebuiltAgentTemplate
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SystemInstruction { get; init; } = string.Empty;
    public string SetupInstructions { get; init; } = string.Empty;
    public IReadOnlyList<string> RequestedToolNames { get; init; } = [];
    public string? ProfilePictureKey { get; init; }
}
