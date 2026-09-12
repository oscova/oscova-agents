using System;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Conversations;

/// <summary>Represents one bounded row in the chat session index.</summary>
internal sealed class ChatSessionIndexEntry
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    public string SessionId { get; set; } = string.Empty;

    public string Channel { get; set; } = ChatChannels.Web;

    public string Title { get; set; } = string.Empty;

    public string? AgentId { get; set; }

    public string? AgentName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsCurrent { get; set; }

    public bool IsUnread { get; set; }

    /// <summary>Gets or sets whether this thread is stored encrypted.</summary>
    public bool IsEncrypted { get; set; }

    public int LastIndexedMessageCount { get; set; }

    public int LastSummarizedMessageCount { get; set; }

    public int LastReflectedMessageCount { get; set; }
}
