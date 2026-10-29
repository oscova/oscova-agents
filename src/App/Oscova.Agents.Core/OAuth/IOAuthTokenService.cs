using Oscova.Agents.Core.Secrets;

namespace Oscova.Agents.Core.OAuth;

public interface IOAuthTokenService
{
    /// <summary>Exchanges a consent code using its server-held PKCE verifier.</summary>
    Task<OAuthTokenData?> ExchangeCodeAsync(string provider, string code, string redirectUri, string codeVerifier, CancellationToken ct = default);
    Task<string?> GetValidAccessTokenAsync(string provider, CancellationToken ct = default);
    Task<OAuthConnectionStatus> GetStatusAsync(string provider, CancellationToken ct = default);
    Task StoreTokenAsync(string provider, OAuthTokenData data, CancellationToken ct = default);
    Task DisconnectAsync(string provider, CancellationToken ct = default);
    Task<string?> GetProviderClientIdAsync(string provider, CancellationToken ct = default);
    Task<bool> HasProviderClientSecretAsync(string provider, CancellationToken ct = default);
    Task<string?> GetProviderClientSecretAsync(string provider, CancellationToken ct = default);
}

public sealed class OAuthConnectionStatus
{
    public bool IsConnected { get; init; }
    public string? ConnectedEmail { get; init; }
}
