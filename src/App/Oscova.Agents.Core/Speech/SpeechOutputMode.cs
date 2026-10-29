namespace Oscova.Agents.Core.Speech;

/// <summary>
/// Defines where synthesized assistant speech should be played for admin chat playback.
/// </summary>
public enum SpeechOutputMode
{
    BrowserOnly = 0,
    BrowserAndAvatar = 1,
    AvatarOnly = 2
}
