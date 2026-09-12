namespace Oscova.Agents.Speech.Contracts;

/// <summary>Describes one voice offered by a speech synthesis provider.</summary>
/// <param name="Id">Stable provider voice identifier.</param>
/// <param name="DisplayName">User-facing voice name.</param>
/// <param name="Language">Optional language or accent.</param>
/// <param name="Gender">Optional provider-reported gender.</param>
public sealed record SpeechSynthesisVoice(
    string Id,
    string DisplayName,
    string? Language = null,
    string? Gender = null);
