using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Oscova.Agents.Conversations;

/// <summary>Represents a persisted multi-turn chat thread.</summary>
public class ChatThread
{
    /// <summary>Gets or sets the thread identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Gets or sets the thread title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the persisted display messages.</summary>
    public IList<ChatDisplayMessage> DisplayMessages { get; set; } = [];

    /// <summary>Gets or sets the serialized framework session state.</summary>
    public JsonElement? SessionElement { get; set; }

    /// <summary>Gets or sets the system instruction captured for the session.</summary>
    public string? SystemInstruction { get; set; }

    /// <summary>Gets or sets the owning agent identifier.</summary>
    public string? AgentId { get; set; }

    /// <summary>Gets or sets the owning agent name.</summary>
    public string? AgentName { get; set; }

    /// <summary>Gets or sets when the thread was last modified.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets whether the thread has unread activity.</summary>
    public bool IsUnread { get; set; }

    /// <summary>Gets or sets whether the thread is current for its agent.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>Gets or sets whether the thread is held in memory only and never persisted.</summary>
    public bool IsIncognito { get; set; }

    /// <summary>Gets or sets whether this thread is stored encrypted.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>Gets or sets the last indexed message count.</summary>
    public int LastIndexedMessageCount { get; set; }
    /// <summary>Gets or sets the last summarized message count.</summary>
    public int LastSummarizedMessageCount { get; set; }
    /// <summary>Gets or sets the last reflected message count.</summary>
    public int LastReflectedMessageCount { get; set; }

    /// <summary>Runs when a submitted elicitation response partitions the current message.</summary>
    public Action? ElicitationGenerated;
}
