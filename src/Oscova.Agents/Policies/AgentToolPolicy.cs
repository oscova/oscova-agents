namespace Oscova.Agents.Policies;

/// <summary>
/// Stores an agent-specific governance override for one qualified tool name.
/// </summary>
public sealed class AgentToolPolicy
{
    /// <summary>
    /// Gets or sets the qualified tool name the override applies to.
    /// </summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the approval behavior for the selected tool.
    /// </summary>
    public ToolApprovalMode ApprovalMode { get; set; } = ToolApprovalMode.Default;
}
