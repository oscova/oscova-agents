using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts;

/// <summary>Reports presentation request acceptance without claiming completed viewport animation.</summary>
public sealed class AvatarPresentationSettingsResponse : IAvatarBridgeResponse
{
    /// <inheritdoc />
    [JsonPropertyName("Success")]
    public bool Success { get; set; }

    /// <inheritdoc />
    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the complete settings accepted by the engine.</summary>
    [JsonPropertyName("Settings")]
    public AvatarPresentationSettingsRequest Settings { get; set; } = new();
}
