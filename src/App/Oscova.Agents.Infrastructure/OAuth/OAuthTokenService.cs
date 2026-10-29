using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.OAuth;
using Oscova.Agents.Credentials;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Infrastructure.Plugins;

namespace Oscova.Agents.Infrastructure.OAuth;

public sealed class OAuthTokenService(
    ICredentialService credentialService,
    OAuthProviderCatalog providerCatalog,
    ISystemCredentialService systemCredentialService,
    IHttpClientFactory httpClientFactory,
    RunContextAccessor runContextAccessor,
    ILogger<OAuthTokenService> logger) : IOAuthTokenService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);

    public async Task<OAuthConnectionStatus> GetStatusAsync(string provider, CancellationToken ct = default)
    {
        var tokenData = await LoadTokenDataAsync(provider, ct).ConfigureAwait(false);
        return tokenData is null
            ? new OAuthConnectionStatus { IsConnected = false }
            : new OAuthConnectionStatus
            {
                IsConnected = true,
                ConnectedEmail = tokenData.ConnectedEmail
            };
    }

    public async Task<string?> GetValidAccessTokenAsync(string provider, CancellationToken ct = default)
    {
        // SDK toolsets bypass the shared HTTP handler, so reject before credentials can enable egress.
        if (runContextAccessor.Current?.AirGapped == true)
        {
            throw new HttpRequestException("Offline mode is on: credential-backed outbound requests are blocked.");
        }

        var normalizedProvider = providerCatalog.Get(provider).Definition.Id;
        var gate = _refreshGates.GetOrAdd(normalizedProvider, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read after entry so a waiter sees the preceding refresh's persisted token.
            var current = await LoadTokenDataAsync(normalizedProvider, ct).ConfigureAwait(false);
            if (current is null)
            {
                return null;
            }

            if (!current.IsExpired)
            {
                return current.AccessToken;
            }

            var refreshed = await RefreshAsync(normalizedProvider, current, ct).ConfigureAwait(false);
            if (refreshed is null)
            {
                return null;
            }

            await StoreTokenAsync(normalizedProvider, refreshed, ct).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<string?> GetProviderClientIdAsync(string provider, CancellationToken ct = default)
    {
        var normalizedProvider = providerCatalog.Get(provider).Definition.Id;
        return systemCredentialService.ResolveFieldAsync(
            ProviderCredentialKey(normalizedProvider), OAuthProviderDefinition.ClientIdField, ct);
    }

    public Task<bool> HasProviderClientSecretAsync(string provider, CancellationToken ct = default)
    {
        var normalizedProvider = providerCatalog.Get(provider).Definition.Id;
        return systemCredentialService.HasValueAsync(
            ProviderCredentialKey(normalizedProvider), OAuthProviderDefinition.ClientSecretField, ct);
    }

    public Task<string?> GetProviderClientSecretAsync(string provider, CancellationToken ct = default)
    {
        var normalizedProvider = providerCatalog.Get(provider).Definition.Id;
        return systemCredentialService.ResolveFieldAsync(
            ProviderCredentialKey(normalizedProvider), OAuthProviderDefinition.ClientSecretField, ct);
    }

    public async Task StoreTokenAsync(string provider, OAuthTokenData data, CancellationToken ct = default)
    {
        var normalizedProvider = providerCatalog.Get(provider).Definition.Id;
        var json = JsonSerializer.Serialize(data, JsonOptions);

        // OAuth tokens live in the same encrypted credential store as other secrets,
        // but they are tagged as managed credentials so the manual credentials UI hides them.
        await credentialService.SaveAsync(
            new CredentialWrite
            {
                CredentialKey = TokenCredentialKey(normalizedProvider),
                Alias = $"{providerCatalog.Get(normalizedProvider).Definition.DisplayName} OAuth",
                Description = "Managed automatically for connected accounts.",
                IsManaged = true,
                Fields =
                [
                    new CredentialFieldWrite
                    {
                        Name = CredentialFieldSets.FreeFormFieldName,
                        IsSecret = true,
                        Source = CredentialFieldSource.Stored,
                        Value = json
                    }
                ]
            },
            ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(string provider, CancellationToken ct = default)
    {
        var normalizedProvider = providerCatalog.Get(provider).Definition.Id;
        // Drain any refresh before deleting so its response cannot reconnect the account.
        var gate = _refreshGates.GetOrAdd(normalizedProvider, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await credentialService.DeleteAsync(TokenCredentialKey(normalizedProvider), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Exchanges a consent code using the verifier bound to its authorization request.</summary>
    public async Task<OAuthTokenData?> ExchangeCodeAsync(string provider, string code, string redirectUri, string codeVerifier, CancellationToken ct = default)
    {
        var clientId = await GetProviderClientIdAsync(provider, ct).ConfigureAwait(false);
        var clientSecret = await GetProviderClientSecretAsync(provider, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        var tokenResponse = await SendTokenRequestAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        }, "code exchange", ct).ConfigureAwait(false);
        if (tokenResponse is null) { return null; }

        // We store a friendly email alongside the tokens so the UI can show which external
        // account is connected without exposing the token payload itself.
        var connectedEmail = await GetConnectedEmailAsync(provider, tokenResponse.AccessToken, ct).ConfigureAwait(false);
        return new OAuthTokenData
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken ?? string.Empty,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 3600),
            ConnectedEmail = connectedEmail
        };
    }

    /// <summary>Reads the connected identity for display beside the stored token.</summary>
    private async Task<string?> GetConnectedEmailAsync(string provider, string accessToken, CancellationToken ct)
    {
        var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            providerCatalog.Get(provider).Definition.UserInfoEndpoint);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "OAuth user info lookup failed for provider '{Provider}' with status code {StatusCode}.",
                provider,
                (int)response.StatusCode);
            return null;
        }

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(JsonOptions, ct).ConfigureAwait(false);
        return document is null ? null
            : NormalizeEmail(providerCatalog.Get(provider).Definition.ReadConnectedEmail(document.RootElement));
    }

    private static string? NormalizeEmail(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<OAuthTokenData?> RefreshAsync(string provider, OAuthTokenData current, CancellationToken ct)
    {
        // Refresh uses the shared provider app credentials rather than anything user-entered.
        var clientId = await GetProviderClientIdAsync(provider, ct).ConfigureAwait(false);
        var clientSecret = await GetProviderClientSecretAsync(provider, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            logger.LogWarning("OAuth refresh skipped because provider '{Provider}' is not configured.", provider);
            return null;
        }

        if (string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            logger.LogWarning("OAuth refresh skipped because provider '{Provider}' has no refresh token.", provider);
            return null;
        }

        var tokenResponse = await SendTokenRequestAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        }, "refresh", ct).ConfigureAwait(false);
        if (tokenResponse is null) { return null; }

        return new OAuthTokenData
        {
            AccessToken = tokenResponse.AccessToken,
            // Some providers rotate refresh tokens during refresh while others omit them.
            // Keep the existing token when the response does not send a replacement.
            RefreshToken = string.IsNullOrWhiteSpace(tokenResponse.RefreshToken)
                ? current.RefreshToken
                : tokenResponse.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 3600),
            ConnectedEmail = current.ConnectedEmail
        };
    }

    /// <summary>Sends both token grants through the same provider HTTP and response boundary.</summary>
    private async Task<OAuthTokenEndpointResponse?> SendTokenRequestAsync(
        string provider, Dictionary<string, string> fields, string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, providerCatalog.Get(provider).Definition.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        var httpClient = httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var level = operation == "refresh" && (int)response.StatusCode is not (>= 400 and < 500)
                ? LogLevel.Error : LogLevel.Warning;
            logger.Log(level, "OAuth {Operation} failed for provider '{Provider}' with status code {StatusCode}.",
                operation, provider, (int)response.StatusCode);
            return null;
        }

        var token = await response.Content.ReadFromJsonAsync<OAuthTokenEndpointResponse>(JsonOptions, ct).ConfigureAwait(false);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            if (operation == "refresh")
            {
                logger.LogWarning("OAuth refresh returned an empty token response for provider '{Provider}'.", provider);
            }

            return null;
        }

        return token;
    }

    private async Task<OAuthTokenData?> LoadTokenDataAsync(string provider, CancellationToken ct)
    {
        var raw = await credentialService.ResolveFieldAsync(TokenCredentialKey(provider), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OAuthTokenData>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "OAuth token payload for provider '{Provider}' could not be parsed.", provider);
            return null;
        }
    }

    private string TokenCredentialKey(string provider)
    {
        var entry = providerCatalog.Get(provider);
        return PluginCredentialKeys.OAuthToken(entry.PluginId, entry.Definition.Id);
    }

    private string ProviderCredentialKey(string provider)
    {
        var entry = providerCatalog.Get(provider);
        return PluginCredentialKeys.Qualify(entry.PluginId, entry.Definition.AppCredential.Key);
    }
}
