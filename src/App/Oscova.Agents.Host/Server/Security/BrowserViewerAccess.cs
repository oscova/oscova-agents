using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Identity;
using System.Globalization;
using Oscova.Agents.Infrastructure.Auth;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>Revokes browser viewers for the signed-out cookie session, including existing circuits.</summary>
public sealed class BrowserViewerAccess(AuthenticationStateProvider authentication, IMemoryCache cache, IServiceScopeFactory scopes)
{
    private const string SessionClaim = "oscova:browser-session";
    private const string ExpiresClaim = "oscova:browser-session-expires";

    /// <summary>Checks the circuit's original session before direct browser workspace calls.</summary>
    public async Task RequireAsync() => await RequireAsync((await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User, cache, scopes).ConfigureAwait(false);

    /// <summary>Checks current account authentication before browser input or continued streaming.</summary>
    public static async Task RequireAsync(ClaimsPrincipal principal, IMemoryCache cache, IServiceScopeFactory scopes)
    {
        Require(principal, cache);
        // A fresh Identity scope observes password changes made while a circuit remains connected.
        using var scope = scopes.CreateScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        if (await signIn.ValidateSecurityStampAsync(principal).ConfigureAwait(false) is null)
        { throw new UnauthorizedAccessException("Sign in again to use the browser workspace."); }
    }

    /// <summary>Rejects expired or signed-out browser sessions without disposing their browser pages.</summary>
    private static void Require(ClaimsPrincipal principal, IMemoryCache cache)
    {
        var session = principal.FindFirstValue(SessionClaim);
        if (principal.Identity?.IsAuthenticated != true || session is null ||
            !long.TryParse(principal.FindFirstValue(ExpiresClaim), out var expires) ||
            expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || cache.TryGetValue((SessionClaim, session), out _))
        { throw new UnauthorizedAccessException("Sign in again to use the browser workspace."); }
    }

    /// <summary>Attaches a revocable identity to each newly issued browser login.</summary>
    public static Task SigningIn(CookieSigningInContext context)
    {
        var identity = (ClaimsIdentity)context.Principal!.Identity!;
        foreach (var claim in identity.FindAll(SessionClaim).Concat(identity.FindAll(ExpiresClaim)).ToArray()) { identity.RemoveClaim(claim); }

        identity.AddClaim(new(SessionClaim, Guid.NewGuid().ToString("N")));
        identity.AddClaim(new(ExpiresClaim, context.Properties.ExpiresUtc!.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        return Task.CompletedTask;
    }

    /// <summary>Retains browser session identity when Identity refreshes account claims.</summary>
    public static async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var session = context.Principal!.FindFirst(SessionClaim);
        if (session is not null && context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>()
            .TryGetValue((SessionClaim, session.Value), out _))
        { context.RejectPrincipal(); return; }
        // Account deletion must revoke a cookie before its next periodic security-stamp check.
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.GetUserAsync(context.Principal!).ConfigureAwait(false) is null)
        { context.RejectPrincipal(); return; }

        await SecurityStampValidator.ValidatePrincipalAsync(context).ConfigureAwait(false);
        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            if (session is not null && !identity.HasClaim(item => item.Type == SessionClaim)) { identity.AddClaim(session); }
            // A new request sees a renewed ticket's expiry. Existing circuits keep their original deadline.
            foreach (var claim in identity.FindAll(ExpiresClaim).ToArray()) { identity.RemoveClaim(claim); }

            identity.AddClaim(new(ExpiresClaim, context.Properties.ExpiresUtc!.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Invalidates the session carried by live streams and circuits at ordinary sign-out.</summary>
    public static Task SigningOut(CookieSigningOutContext context)
    {
        var principal = context.HttpContext.User;
        if (principal.FindFirstValue(SessionClaim) is { } session &&
            long.TryParse(principal.FindFirstValue(ExpiresClaim), out var expires) && expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>()
                .Set((SessionClaim, session), true, DateTimeOffset.FromUnixTimeSeconds(expires));
        }

        return Task.CompletedTask;
    }
}
