namespace Oscova.Agents.Core.Speech;

/// <summary>Identifies a prepared speech clip for delivery by the initiating chat panel.</summary>
public sealed record SpeechPlaybackReceipt(string ThreadId, string Text, string FileUrl, double DurationMs, SpeechOutputTarget Targets);
