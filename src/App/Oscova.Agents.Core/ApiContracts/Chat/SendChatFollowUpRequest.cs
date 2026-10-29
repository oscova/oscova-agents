using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Carries a web follow-up typed while a chat run may still be live.</summary>
public sealed class SendChatFollowUpRequest
{
    public required string MessageId { get; init; }

    public string Text { get; init; } = string.Empty;

    public ChatFollowUpMode Mode { get; init; }

    public string? AgentId { get; init; }

    /// <summary>Keeps a queued or steered message bound to its selected recipient.</summary>
    public string? RecipientAgentId { get; init; }

    public string? ModelProviderId { get; init; }

    public ToolApprovalRunMode ApprovalRunMode { get; init; }
    public IReadOnlyList<WorkspaceFileReference> WorkspaceFiles { get; init; } = [];
}
