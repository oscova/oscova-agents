using System;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Conversations;

/// <summary>Represents one versioned row in a chat display transcript.</summary>
internal sealed class ChatTranscriptRecord
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    public string Kind { get; set; } = "message";

    public string SessionId { get; set; } = string.Empty;

    public string Channel { get; set; } = ChatChannels.Web;

    public string? AgentId { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public string? SystemInstruction { get; set; }

    public ChatDisplayMessage? Message { get; set; }

    /// <summary>Gets or sets whether this record's content is stored encrypted.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>Gets or sets the protected content when <see cref="IsEncrypted"/> is set.</summary>
    public string? ProtectedPayload { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
