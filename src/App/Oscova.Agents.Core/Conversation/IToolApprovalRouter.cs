using Microsoft.Extensions.AI;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Routes a tool approval request to the correct channel implementation.
/// </summary>
public interface IToolApprovalRouter
{
    Task<ToolApprovalSubmission> RouteAsync(
        ToolApprovalRequestContent request,
        ToolApprovalContext context,
        CancellationToken cancellationToken);
}

public sealed class ToolApprovalContext
{
    public required string UserId { get; init; }
    public required ToolApprovalChannel Channel { get; init; }
    public required ToolApprovalRunMode RunMode { get; init; }
    public object? ChannelMetadata { get; init; }
}