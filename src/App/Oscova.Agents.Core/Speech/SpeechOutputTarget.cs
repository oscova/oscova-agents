namespace Oscova.Agents.Core.Speech;

/// <summary>
/// Represents the concrete playback targets selected for a speech request.
/// </summary>
[Flags]
public enum SpeechOutputTarget
{
    None = 0,
    Browser = 1,
    Avatar = 2
}
