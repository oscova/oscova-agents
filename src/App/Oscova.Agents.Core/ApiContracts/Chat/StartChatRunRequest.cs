using Oscova.Agents.Conversations;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Describes one web chat run to admit into server-owned execution.</summary>
public sealed class StartChatRunRequest
{
    public required string RunId { get; init; }

    public required string UserMessageId { get; init; }

    public string UserText { get; init; } = string.Empty;

    public string? AgentId { get; init; }

    /// <summary>Targets one accessible guest without changing the thread's owning agent.</summary>
    public string? RecipientAgentId { get; init; }

    public string? ModelProviderId { get; init; }

    public ToolApprovalRunMode ApprovalRunMode { get; init; }

    public IReadOnlyList<string> AttachmentIds { get; init; } = [];

    public string? RegenerateAssistantMessageId { get; init; }

    /// <summary>Continues saved work without submitting another user message.</summary>
    public bool ContinueSession { get; init; }

    /// <summary>Requests an owner-authorized budget exception for this operation only.</summary>
    public bool ContinueOverBudget { get; set; }

    /// <summary>Correlates answers with a finalized question card in this conversation.</summary>
    public UserQuestionAnswer? QuestionAnswer { get; init; }
    public IReadOnlyList<WorkspaceFileReference> WorkspaceFiles { get; init; } = [];
}
