using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts;

/// <summary>Reports whether a gesture started, and its duration.</summary>
public class AvatarGestureResponse : IAvatarBridgeResponse
{
    /// <summary>Whether the request succeeded.</summary>
    [JsonPropertyName("Success")]
    public bool Success { get; set; }

    /// <summary>The operation result or actionable failure.</summary>
    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The selected name on success.</summary>
    [JsonPropertyName("SelectedGesture")]
    public string SelectedGesture { get; set; } = string.Empty;

    /// <summary>Animation duration in seconds, or zero when stopped or rejected.</summary>
    [JsonPropertyName("DurationSeconds")]
    public float DurationSeconds { get; set; }
}
