using System;
using System.Collections.Generic;

namespace Oscova.Agents.Conversations;

/// <summary>Represents a persisted display message with parsed segments and timing data.</summary>
public class ChatDisplayMessage
{
    /// <summary>Gets or sets the messaging channel that produced this display message.</summary>
    public string Channel { get; set; } = ChatChannels.Web;

    /// <summary>Gets or sets the message identifier.</summary>
    public string MessageId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Gets or sets when the message was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the owner of the message.</summary>
    public MessageOwner From { get; set; } = MessageOwner.Bot;

    /// <summary>Identifies the agent that produced an assistant message so artifacts retain their original file grants.</summary>
    public string? AgentId { get; set; }

    /// <summary>Retains the displayed speaker name independently of the thread owner.</summary>
    public string? AgentName { get; set; }

    /// <summary>Identifies the selected recipient of a user message for replay and regeneration.</summary>
    public string? RecipientAgentId { get; set; }

    /// <summary>Gets or sets whether the message continues an active interaction.</summary>
    public bool IsInteractionResponse { get; set; }

    /// <summary>Gets or sets whether earlier history was automatically summarized while producing this reply.</summary>
    public bool HistorySummarized { get; set; }

    /// <summary>Gets or sets direct message text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the parsed and timed message segments.</summary>
    public IList<ChatSegment> Segments { get; set; } = [];

    /// <summary>Gets or sets an optional elicitation payload.</summary>
    public ElicitationRequest? Elicitation { get; set; }

    /// <summary>Gets or sets an optional tool approval payload.</summary>
    public ToolApprovalRequest? ToolApproval { get; set; }

    /// <summary>Gets or sets the user-supplied attachments staged for this message.</summary>
    public IReadOnlyList<ChatAttachment> Attachments { get; set; } = [];

    /// <summary>Gets or sets the reported input-token count.</summary>
    public long? InputTokens { get; set; }

    /// <summary>Gets or sets the reported output-token count.</summary>
    public long? OutputTokens { get; set; }

    /// <summary>Gets or sets the reported total-token count.</summary>
    public long? TotalTokens { get; set; }
}
