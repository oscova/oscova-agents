using System;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Speech.Contracts;

/// <summary>
/// Contains the audio and metadata returned from speech synthesis.
/// </summary>
public sealed class SpeechSynthesisResponse
{
    /// <summary>
    /// Gets the synthesized audio bytes.
    /// </summary>
    [JsonPropertyName("audio")]
    public byte[] Audio { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Gets the audio sample rate.
    /// </summary>
    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; init; }

    /// <summary>
    /// Gets the number of audio channels.
    /// </summary>
    [JsonPropertyName("channels")]
    public short Channels { get; init; }

    /// <summary>
    /// Gets the audio bit depth.
    /// </summary>
    [JsonPropertyName("bitsPerSample")]
    public short BitsPerSample { get; init; }

    /// <summary>
    /// Gets the total audio sample count.
    /// </summary>
    [JsonPropertyName("totalSamples")]
    public int TotalSamples { get; init; }

    /// <summary>
    /// Gets the voice identifier used for synthesis.
    /// </summary>
    [JsonPropertyName("voiceId")]
    public string VoiceId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the synthesis duration in milliseconds.
    /// </summary>
    [JsonPropertyName("durationMs")]
    public double DurationMs { get; init; }

    /// <summary>
    /// Gets or sets the optional cached audio file URL supplied by a provider or populated by the Host.
    /// </summary>
    [JsonPropertyName("fileUrl")]
    public string? FileUrl { get; set; }
}
