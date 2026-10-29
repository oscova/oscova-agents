using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Policies;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Coordinates pending Web UI tool approvals for the current user scope.
/// </summary>
public interface IWebToolApprovalService
{
    ToolApprovalRequest RegisterToolApproval(
        ToolApprovalRequestContent request,
        ToolApprovalRequestContent displaySource,
        ToolApprovalReview? review,
        ChatThread chatThread,
        Action onStateChanged);

    Task<ToolApprovalSubmission> WaitForToolApprovalAsync(
        string requestId,
        CancellationToken token);

    Task SubmitToolApprovalAsync(string requestId, ToolApprovalDecision decision);

    ToolApprovalSubmission? TryAutoApprove(ToolApprovalRequestContent request, ChatThread chatThread,
        Action onStateChanged, ToolApprovalRunMode mode);

    IReadOnlyList<SessionToolPermission> GetSessionPermissions(string conversationId);

    void RevokeSessionPermission(string conversationId, string? grantId);

    bool IsPending(string requestId);

    void CancelPendingToolApproval(string requestId);

    void CancelAllPendingToolApprovals();
}

public sealed record ToolApprovalSubmission(
    ToolApprovalResponseContent Response,
    ChatDisplayMessage? UserResponseMessage);
