namespace Oscova.Agents.Policies;

/// <summary>
/// Controls how approval-required tool calls are handled for one run.
/// </summary>
public enum ToolApprovalRunMode
{
    /// <summary>
    /// Apply each tool's effective agent policy during the run.
    /// </summary>
    UseAgentPolicy = 0,

    /// <summary>
    /// Automatically approve tool calls that would normally require user approval.
    /// </summary>
    AutoApproveRequired = 1,

    /// <summary>
    /// Automatically reject tool calls that would normally require user approval.
    /// </summary>
    AutoRejectRequired = 2
}
