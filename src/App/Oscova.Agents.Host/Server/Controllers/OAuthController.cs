using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Oscova.Agents.Core.ApiContracts.OAuth;
using Oscova.Agents.Core.OAuth;
using Oscova.Agents.Credentials;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Infrastructure.OAuth;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/oauth")]
[IgnoreAntiforgeryToken]
public sealed class OAuthController(
    UserServiceManager userServiceManager,
    OAuthStateStore stateStore,
    OAuthProviderCatalog providerCatalog,
    ISystemCredentialService systemCredentialService,
    ILogger<OAuthController> logger)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpGet("status")]
    public async Task<ActionResult<OAuthStatusResponse>> GetStatus(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var providers = new List<OAuthProviderStatus>();
        foreach (var entry in providerCatalog.Providers)
        {
            var definition = entry.Definition;
            var status = await userScope.OAuthTokenService.GetStatusAsync(definition.Id, ct);
            providers.Add(new OAuthProviderStatus
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                Services = definition.Services,
                Description = definition.Description,
                IsConfigured = await IsProviderConfiguredAsync(userScope.OAuthTokenService, definition.Id, ct),
                IsConnected = status.IsConnected,
                ConnectedEmail = status.ConnectedEmail
            });
        }

        return Ok(new OAuthStatusResponse { Providers = providers });
    }

    [HttpGet("{provider}/authorize")]
    public async Task<IActionResult> AuthorizeProvider(string provider, CancellationToken ct)
    {
        var normalizedProvider = TryNormalizeProvider(provider);
        if (normalizedProvider is null)
        {
            return BadRequest("Unsupported provider.");
        }

        var userScope = await GetUserScopeAsync(ct);
        if (!await IsProviderConfiguredAsync(userScope.OAuthTokenService, normalizedProvider, ct))
        {
            return BadRequest("Provider not configured.");
        }

        // The state value ties the callback to the authenticated user who started the flow
        // and gives us a CSRF check before exchanging the authorization code.
        var state = stateStore.Generate(GetUserId(), normalizedProvider, out var codeChallenge);
        var redirectUri = BuildRedirectUri(Request, normalizedProvider);
        var clientId = await userScope.OAuthTokenService.GetProviderClientIdAsync(normalizedProvider, ct);
        var authorizationUrl = BuildAuthorizationUrl(
            normalizedProvider,
            clientId ?? string.Empty,
            redirectUri,
            state,
            codeChallenge);

        return Redirect(authorizationUrl);
    }

    [HttpGet("{provider}/callback")]
    public async Task<IActionResult> Callback(
        string provider,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        var normalizedProvider = TryNormalizeProvider(provider);
        if (normalizedProvider is null)
        {
            return Redirect(BuildAppRedirect(Request, "unknown_provider", provider));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return Redirect(BuildAppRedirect(Request, error, normalizedProvider));
        }

        if (!stateStore.TryConsume(state ?? string.Empty, out var initiated))
        {
            return Redirect(BuildAppRedirect(Request, "invalid_state", normalizedProvider));
        }

        // The callback still runs under the current app session, so we verify that the
        // same authenticated user who started the redirect is the one completing it.
        var currentUserId = GetUserId();
        if (!string.Equals(currentUserId, initiated.UserId, StringComparison.Ordinal) ||
            !string.Equals(normalizedProvider, initiated.ProviderId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "OAuth callback identity mismatch for provider '{Provider}'. Initiated '{InitiatedUserId}', current '{CurrentUserId}'.",
                normalizedProvider,
                initiated.UserId,
                currentUserId);
            return Redirect(BuildAppRedirect(Request, "invalid_state", normalizedProvider));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Redirect(BuildAppRedirect(Request, "missing_code", normalizedProvider));
        }

        var userScope = await GetUserScopeAsync(ct);
        if (!await IsProviderConfiguredAsync(userScope.OAuthTokenService, normalizedProvider, ct))
        {
            return Redirect(BuildAppRedirect(Request, "provider_not_configured", normalizedProvider));
        }

        var redirectUri = BuildRedirectUri(Request, normalizedProvider);
        var tokenData = await userScope.OAuthTokenService.ExchangeCodeAsync(
            normalizedProvider, code, redirectUri, initiated.CodeVerifier, ct);
        if (tokenData is null)
        {
            return Redirect(BuildAppRedirect(Request, "token_exchange_failed", normalizedProvider));
        }

        try
        {
            await userScope.OAuthTokenService.StoreTokenAsync(normalizedProvider, tokenData, ct);
        }
        catch (ConfidentialDataLockedException)
        {
            return Redirect(BuildAppRedirect(Request, "confidential_data_locked", normalizedProvider));
        }

        return Redirect(BuildAppRedirect(Request, null, normalizedProvider));
    }

    [HttpPost("{provider}/disconnect")]
    public async Task<IActionResult> Disconnect(string provider, CancellationToken ct)
    {
        var normalizedProvider = TryNormalizeProvider(provider);
        if (normalizedProvider is null)
        {
            return BadRequest("Unsupported provider.");
        }

        var userScope = await GetUserScopeAsync(ct);
        await userScope.OAuthTokenService.DisconnectAsync(normalizedProvider, ct);
        return NoContent();
    }

    [HttpGet("admin/providers")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<OAuthProviderAdminConfiguration>> GetAdminProviders(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var providers = new List<OAuthProviderAdminEntry>();
        foreach (var entry in providerCatalog.Providers)
        {
            providers.Add(await BuildAdminEntryAsync(userScope.OAuthTokenService, entry.Definition, ct));
        }

        return Ok(new OAuthProviderAdminConfiguration { Providers = providers });
    }

    [HttpPut("admin/providers/{provider}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SaveAdminProvider(string provider, [FromBody] SaveOAuthProviderRequest request, CancellationToken ct)
    {
        var normalizedProvider = TryNormalizeProvider(provider);
        if (normalizedProvider is null)
        {
            return BadRequest("Unsupported provider.");
        }

        var credentialKey = ProviderCredentialKey(normalizedProvider);
        var normalizedClientId = NormalizeOptional(request.ClientId);

        // Clearing the client id disables the provider entirely.
        if (normalizedClientId is null)
        {
            var existing = await systemCredentialService.GetByKeyAsync(credentialKey, ct);
            if (existing is not null)
            {
                await systemCredentialService.DeleteAsync(credentialKey, ct);
            }

            return NoContent();
        }

        var hasSecret = await systemCredentialService.HasValueAsync(credentialKey, OAuthProviderDefinition.ClientSecretField, ct);
        var normalizedClientSecret = NormalizeOptional(request.ClientSecret);
        if (!hasSecret && normalizedClientSecret is null)
        {
            return BadRequest("A client secret is required for the first save.");
        }

        var fields = new List<CredentialFieldWrite>
        {
            new()
            {
                Name = OAuthProviderDefinition.ClientIdField,
                IsSecret = false,
                Source = CredentialFieldSource.Stored,
                Value = normalizedClientId
            },
            new()
            {
                Name = OAuthProviderDefinition.ClientSecretField,
                IsSecret = true,
                Source = CredentialFieldSource.Stored,
                Value = normalizedClientSecret
            }
        };

        // Empty incoming secret means "keep the current stored secret"; CredentialService
        // can apply that rule only when the existing secret field remains in this write.

        try
        {
            await systemCredentialService.SaveAsync(
                GetUserId(),
                new CredentialWrite
                {
                    CredentialKey = credentialKey,
                    Alias = $"{providerCatalog.Get(normalizedProvider).Definition.DisplayName} OAuth",
                    Description = "Client id and secret for the shared OAuth app.",
                    IsManaged = false,
                    Fields = fields
                },
                ct);
        }
        catch (ConfidentialDataLockedException)
        {
            return StatusCode(
                StatusCodes.Status423Locked,
                "Create or unlock your Vault Password before saving stored secrets.");
        }

        return NoContent();
    }

    private static async Task<OAuthProviderAdminEntry> BuildAdminEntryAsync(IOAuthTokenService oAuthTokenService, OAuthProviderDefinition definition, CancellationToken ct)
    {
        var clientId = await oAuthTokenService.GetProviderClientIdAsync(definition.Id, ct);
        var hasSecret = await oAuthTokenService.HasProviderClientSecretAsync(definition.Id, ct);
        return new OAuthProviderAdminEntry
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            Services = definition.Services,
            Description = definition.Description,
            Fields = definition.AppCredential.Fields,
            ClientId = clientId,
            HasClientSecret = hasSecret,
            IsConfigured = !string.IsNullOrWhiteSpace(clientId) && hasSecret
        };
    }

    private static string BuildRedirectUri(HttpRequest request, string provider)
    {
        return $"{request.Scheme}://{request.Host}{request.PathBase}/api/oauth/{provider}/callback";
    }

    private string BuildAuthorizationUrl(string provider, string clientId, string redirectUri, string state, string codeChallenge)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', providerCatalog.Get(provider).Definition.Scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        var definition = providerCatalog.Get(provider).Definition;
        foreach (var parameter in definition.AuthorizationParameters)
        {
            parameters.Add(parameter.Key, parameter.Value);
        }

        return QueryHelpers.AddQueryString(definition.AuthorizationEndpoint.AbsoluteUri, parameters);
    }

    private static string BuildAppRedirect(HttpRequest request, string? error, string provider)
    {
        var appPath = request.PathBase.HasValue ? request.PathBase.Value! : "/";
        var parameters = new Dictionary<string, string?>
        {
            ["settings"] = "connectedaccounts",
            ["oauth_provider"] = provider
        };

        if (string.IsNullOrWhiteSpace(error))
        {
            parameters["oauth_connected"] = provider;
        }
        else
        {
            parameters["oauth_error"] = error;
        }

        return QueryHelpers.AddQueryString(appPath, parameters);
    }

    private static async Task<bool> IsProviderConfiguredAsync(IOAuthTokenService oAuthTokenService, string provider, CancellationToken ct)
    {
        var clientId = await oAuthTokenService.GetProviderClientIdAsync(provider, ct);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }

        return await oAuthTokenService.HasProviderClientSecretAsync(provider, ct);
    }

    private string ProviderCredentialKey(string provider)
    {
        var entry = providerCatalog.Get(provider);
        return PluginCredentialKeys.Qualify(entry.PluginId, entry.Definition.AppCredential.Key);
    }

    private string? TryNormalizeProvider(string provider)
    {
        return providerCatalog.Find(provider)?.Definition.Id;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
