using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>Metadata for a single available tool shown in the agent editor.</summary>
public sealed class ToolDefinition
{
    public string QualifiedName { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ToolApprovalMode DefaultApprovalMode { get; set; } = ToolApprovalMode.Default;

    /// <summary>The tool's declared relationship to network access.</summary>
    public NetworkScope NetworkScope { get; set; } = NetworkScope.None;
}
