using Oscova.Agents.Conversations;
namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Shows one queued follow-up in the chat queue panel.</summary>
public sealed class ChatFollowUpView
{
    public required string MessageId { get; init; }

    public required string Text { get; init; }

    public string? RecipientAgentId { get; init; }
    public IReadOnlyList<WorkspaceFileReference> WorkspaceFiles { get; init; } = [];
}
