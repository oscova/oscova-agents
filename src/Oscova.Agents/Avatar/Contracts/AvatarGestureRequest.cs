using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts;

/// <summary>Selects a stationary gesture reported by the Engine.</summary>
public class AvatarGestureRequest
{
    /// <summary>Case-sensitive catalog name. None stops the current gesture.</summary>
    [JsonPropertyName("GestureName")]
    public string GestureName { get; set; } = string.Empty;
}
