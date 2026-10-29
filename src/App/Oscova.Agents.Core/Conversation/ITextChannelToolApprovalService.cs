using Microsoft.Extensions.AI;
using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Tool-approval handler for text chat channels (Discord, Telegram).
/// </summary>
public interface ITextChannelToolApprovalService
{
    Task<ToolApprovalSubmission> HandleApprovalAsync(
        ToolApprovalRequestContent request,
        ToolApprovalRequestContent displaySource,
        ToolApprovalReview? review,
        PluginElicitationOrigin origin,
        CancellationToken cancellationToken);
}
