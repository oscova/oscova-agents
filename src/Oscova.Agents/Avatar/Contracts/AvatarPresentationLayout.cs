using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts;

/// <summary>Defines the camera and message appearance for one Avatar Engine layout.</summary>
public sealed class AvatarPresentationLayout
{
    /// <summary>Gets or sets the engine camera yaw value, from -5 to 5.</summary>
    [JsonRequired, JsonPropertyName("Yaw")]
    public float Yaw { get; set; }

    /// <summary>Gets or sets the message box's normalized left position.</summary>
    [JsonRequired, JsonPropertyName("PositionX")]
    public float PositionX { get; set; } = 0.25f;

    /// <summary>Gets or sets the message box's normalized top position.</summary>
    [JsonRequired, JsonPropertyName("PositionY")]
    public float PositionY { get; set; } = 0.75f;

    /// <summary>Gets or sets the message box's normalized width.</summary>
    [JsonRequired, JsonPropertyName("Width")]
    public float Width { get; set; } = 0.5f;

    /// <summary>Gets or sets the message box's normalized height.</summary>
    [JsonRequired, JsonPropertyName("Height")]
    public float Height { get; set; } = 0.2f;

    /// <summary>Gets or sets font size, from 8 to 120.</summary>
    [JsonRequired, JsonPropertyName("FontSize")]
    public int FontSize { get; set; } = 32;

    /// <summary>Gets or sets the red channel, from 0 to 1.</summary>
    [JsonRequired, JsonPropertyName("Red")]
    public float Red { get; set; } = 1f;

    /// <summary>Gets or sets the green channel, from 0 to 1.</summary>
    [JsonRequired, JsonPropertyName("Green")]
    public float Green { get; set; } = 1f;

    /// <summary>Gets or sets the blue channel, from 0 to 1.</summary>
    [JsonRequired, JsonPropertyName("Blue")]
    public float Blue { get; set; } = 1f;

    /// <summary>Gets or sets text opacity, from 0 to 1.</summary>
    [JsonRequired, JsonPropertyName("Alpha")]
    public float Alpha { get; set; } = 1f;

    /// <summary>Copies this layout so draft edits cannot change saved values.</summary>
    public AvatarPresentationLayout Clone() => (AvatarPresentationLayout)MemberwiseClone();
}
