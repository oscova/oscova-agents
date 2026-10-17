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

    /// <summary>Gets or sets whether an earlier display page exists.</summary>
    public bool HasOlderDisplayMessages { get; set; }

    /// <summary>Gets or sets the serialized framework session state.</summary>
    public JsonElement? SessionElement { get; set; }

    /// <summary>Gets or sets the immediate source and copied-history boundary.</summary>
    public ChatBranchOrigin? BranchOrigin { get; set; }

    /// <summary>Gets or sets whether the source remains accessible to this user.</summary>
    public bool SourceThreadAvailable { get; set; }

    /// <summary>Gets or sets the reply identities with retained session checkpoints.</summary>
    public IReadOnlyList<string> BranchableMessageIds { get; set; } = [];

    /// <summary>Gets or sets server-owned checkpoints, or null to preserve stored checkpoints on save.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, JsonElement>? SessionCheckpoints { get; set; }

    /// <summary>Gets or sets the system instruction captured for the session.</summary>
    public string? SystemInstruction { get; set; }

    /// <summary>Gets or sets the owning agent identifier.</summary>
    public string? AgentId { get; set; }

    /// <summary>Gets or sets the optional project reference used to scope conversation selection.</summary>
    public string? ProjectId { get; set; }

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


    /// <summary>Gets or sets the last indexed message count.</summary>
    public int LastIndexedMessageCount { get; set; }
    /// <summary>Gets or sets the last summarized message count.</summary>
    public int LastSummarizedMessageCount { get; set; }
    /// <summary>Gets or sets the consumed text offset in the next unsummarized message's formatted conversation line.</summary>
    public int LastSummarizedMessageOffset { get; set; }
    /// <summary>Gets or sets the last reflected message count.</summary>
    public int LastReflectedMessageCount { get; set; }
    /// <summary>Gets or sets the consumed text offset in the next unreflected message's formatted conversation line.</summary>
    public int LastReflectedMessageOffset { get; set; }

    /// <summary>Runs when a submitted elicitation response partitions the current message.</summary>
    public Action? ElicitationGenerated;
}
