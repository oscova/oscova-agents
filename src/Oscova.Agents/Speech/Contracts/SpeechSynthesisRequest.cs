using System.Text.Json.Serialization;

namespace Oscova.Agents.Speech.Contracts;

/// <summary>
/// Describes one speech synthesis request.
/// </summary>
public sealed class SpeechSynthesisRequest
{
    /// <summary>
    /// Gets or sets the text to synthesize.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the requested voice identifier.
    /// </summary>
    [JsonPropertyName("voiceId")]
    public string? VoiceId { get; set; }

    /// <summary>
    /// Gets or sets an optional model path override.
    /// </summary>
    [JsonPropertyName("modelPath")]
    public string? ModelPath { get; set; }

    /// <summary>
    /// Gets or sets whether the returned audio includes the WAV header.
    /// </summary>
    [JsonPropertyName("includeWavHeader")]
    public bool IncludeWavHeader { get; set; } = true;
}
