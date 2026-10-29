namespace Oscova.Agents.Host.Services.UI;

/// <summary>
/// The discrete UI moments that can play a subtle sound cue. Each value maps to a
/// <c>wwwroot/sounds/&lt;id&gt;.wav</c> file by its lowercased name.
/// </summary>
public enum SoundCue
{
    Send,
    Enter,
    Receive,
    Error,
    Notify,
    Tap,
    Complete
}
