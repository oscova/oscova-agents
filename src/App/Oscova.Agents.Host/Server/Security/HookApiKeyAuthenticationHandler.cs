using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Oscova.Agents.Core.Hooks;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>
/// Authenticates inbound hook API callers using system-scoped bearer API keys.
/// </summary>
public sealed class HookApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHookApiKeyService apiKeyService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string KeyIdClaim = "oscova_hook_key_id";
    public const string ScopeClaim = "oscova_hook_scope";
    public const string AllowedTaskSlugClaim = "oscova_hook_allowed_task_slug";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorizationHeader = Request.Headers.Authorization.ToString();
        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        var result = await apiKeyService.ValidateAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (!result.IsValid || string.IsNullOrWhiteSpace(result.KeyId))
        {
            return AuthenticateResult.Fail("Invalid hook API key.");
        }

        var claims = new List<Claim> { new(KeyIdClaim, result.KeyId) };
        claims.AddRange(result.Scopes.Select(scope => new Claim(ScopeClaim, scope)));
        claims.AddRange(result.AllowedTaskSlugs.Select(slug => new Claim(AllowedTaskSlugClaim, slug)));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}