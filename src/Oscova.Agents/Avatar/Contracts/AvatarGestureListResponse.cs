using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts;

/// <summary>Lists the stationary gestures reported by the Engine.</summary>
public class AvatarGestureListResponse
{
    /// <summary>Whether the request succeeded.</summary>
    [JsonPropertyName("Success")]
    public bool Success { get; set; }

    /// <summary>The operation result or actionable failure.</summary>
    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Names accepted by gesture selection, including None.</summary>
    [JsonPropertyName("Gestures")]
    public IReadOnlyList<string> Gestures { get; set; } = [];
}
