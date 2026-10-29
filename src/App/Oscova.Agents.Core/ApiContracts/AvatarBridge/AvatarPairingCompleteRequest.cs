namespace Oscova.Agents.Core.ApiContracts.AvatarBridge;

/// <summary>Completes Avatar Engine pairing without exposing its issued credential.</summary>
public sealed class AvatarPairingCompleteRequest
{
    public string BaseUrl { get; init; } = string.Empty;

    public string PairingCode { get; init; } = string.Empty;
}
