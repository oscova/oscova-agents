using System;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Conversations;

/// <summary>Represents one versioned row in a chat display transcript.</summary>
internal sealed class ChatTranscriptRecord
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")]
    public int Version { get; set; } = CurrentVersion;

    public string Kind { get; set; } = "message";

    public string SessionId { get; set; } = string.Empty;

    public string Channel { get; set; } = ChatChannels.Web;

    public ChatBranchOrigin? BranchOrigin { get; set; }

    public string? AgentId { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public string? SystemInstruction { get; set; }

    public ChatDisplayMessage? Message { get; set; }



    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
