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

    public ChatBranchOrigin? BranchOrigin { get; set; }

    public string? AgentId { get; set; }

    public string? ProjectId { get; set; }

    public string? AgentName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsCurrent { get; set; }

    public bool IsUnread { get; set; }


    public int LastIndexedMessageCount { get; set; }

    public int LastSummarizedMessageCount { get; set; }
    public int LastSummarizedMessageOffset { get; set; }

    public int LastReflectedMessageCount { get; set; }
    public int LastReflectedMessageOffset { get; set; }
}
