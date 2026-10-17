using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts;

/// <summary>Applies both Avatar Engine layouts, their selection mode and message timeout together.</summary>
public sealed class AvatarPresentationSettingsRequest
{
    /// <summary>Gets or sets Auto, Subtitle or Side selection.</summary>
    [JsonRequired, JsonPropertyName("Mode")]
    public string Mode { get; set; } = "Subtitle";

    /// <summary>Gets or sets the message timeout in seconds, with zero disabling ordinary messages.</summary>
    [JsonRequired, JsonPropertyName("DurationSeconds")]
    public float DurationSeconds { get; set; } = 5f;

    /// <summary>Gets or sets the subtitle layout used before Auto selects Side.</summary>
    [JsonRequired, JsonPropertyName("Subtitle")]
    public AvatarPresentationLayout Subtitle { get; set; } = new();

    /// <summary>Gets or sets the side layout used after subtitle overflow.</summary>
    [JsonRequired, JsonPropertyName("Side")]
    public AvatarPresentationLayout Side { get; set; } = new();

    /// <summary>Copies both layouts so a preview can change mode without modifying the saved draft.</summary>
    public AvatarPresentationSettingsRequest Clone() => new()
    {
        Mode = Mode,
        DurationSeconds = DurationSeconds,
        Subtitle = Subtitle.Clone(),
        Side = Side.Clone()
    };
}
