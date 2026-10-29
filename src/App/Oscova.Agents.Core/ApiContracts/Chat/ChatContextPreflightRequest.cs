using Oscova.Agents.Conversations;
namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Requests a side-effect-free estimate for the selected chat context and optional draft.</summary>
public sealed class ChatContextPreflightRequest
{
    /// <summary>
    /// Gets or sets the agent selected for the pending run.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>Estimates the guest's context instead of the thread owner's context.</summary>
    public string? RecipientAgentId { get; init; }

    /// <summary>Gets the project selected for an unsaved draft.</summary>
    public string? ProjectId { get; init; }

    /// <summary>
    /// Gets or sets the explicit model provider selected for the pending run.
    /// </summary>
    public string? ModelProviderId { get; init; }

    /// <summary>
    /// Gets or sets the user prompt text currently in the composer.
    /// </summary>
    public string PromptText { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets browser-side attachments that have not yet been uploaded.
    /// </summary>
    public IReadOnlyList<PendingChatAttachmentSummary> Attachments { get; init; } = [];
    public IReadOnlyList<WorkspaceFileReference> WorkspaceFiles { get; init; } = [];
}
