namespace Oscova.Agents.Policies;

/// <summary>
/// Describes how an agent exposes a selected tool at runtime.
/// </summary>
public enum ToolApprovalMode
{
    /// <summary>
    /// Use the tool's declared default policy.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Require user approval before executing the tool.
    /// </summary>
    RequireApproval = 1,

    /// <summary>
    /// Allow the tool to execute without prompting.
    /// </summary>
    AutoApprove = 2,

    /// <summary>
    /// Prevent the agent from executing the tool.
    /// </summary>
    Deny = 3
}
