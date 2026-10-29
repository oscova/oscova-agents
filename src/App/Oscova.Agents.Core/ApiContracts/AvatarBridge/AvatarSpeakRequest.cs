namespace Oscova.Agents.Core.ApiContracts.AvatarBridge;

/// <summary>Requests synthesized avatar speech from the operator's configured provider.</summary>
public sealed class AvatarSpeakRequest
{
    public string Text { get; set; } = string.Empty;
    public string? ExpectedAppDataPath { get; set; }
}
