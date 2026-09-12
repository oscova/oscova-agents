using System.Collections.Generic;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Persistence;

/// <summary>
/// YAML sidecar shape for sparse per-agent tool governance overrides.
/// </summary>
public sealed class AgentToolPoliciesFile
{
    /// <summary>
    /// Gets or sets the persisted sparse tool policy entries.
    /// </summary>
    public IList<AgentToolPolicyFile> Tools { get; set; } = [];
}

/// <summary>
/// YAML shape for one tool governance override.
/// </summary>
public sealed class AgentToolPolicyFile
{
    /// <summary>
    /// Gets or sets the qualified tool name the policy applies to.
    /// </summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the approval behavior serialized for the tool.
    /// </summary>
    public ToolApprovalMode ApprovalMode { get; set; } = ToolApprovalMode.Default;
}
