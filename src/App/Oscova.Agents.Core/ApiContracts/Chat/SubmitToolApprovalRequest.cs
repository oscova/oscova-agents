namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>
/// User decision for a pending tool approval request.
/// </summary>
public sealed class SubmitToolApprovalRequest
{
    public ToolApprovalDecision Decision { get; set; }
}
