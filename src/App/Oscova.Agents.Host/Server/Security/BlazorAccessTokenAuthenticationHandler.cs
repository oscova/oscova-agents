using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Oscova.Agents.Infrastructure.Auth;

namespace Oscova.Agents.Host.Server.Security;

public sealed class BlazorAccessTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    BlazorAccessTokenService accessTokenService,
    UserManager<ApplicationUser> userManager)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorizationHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.Fail("Missing bearer token.");
        }

        var payload = accessTokenService.ValidateToken(token);
        if (payload is null || string.IsNullOrWhiteSpace(payload.UserId))
        {
            return AuthenticateResult.Fail("Invalid bearer token.");
        }

        // An existing circuit can retain its token after the operator deletes its account.
        var user = await userManager.FindByIdAsync(payload.UserId).ConfigureAwait(false);
        if (user is null)
        {
            return AuthenticateResult.Fail("Invalid bearer token.");
        }

        if (payload.CliTokenVersion is int tokenVersion)
        {
            if (user.CliTokenVersion != tokenVersion)
            {
                return AuthenticateResult.Fail("Invalid bearer token.");
            }

            var roles = await userManager.GetRolesAsync(user).ConfigureAwait(false);
            payload.Name = user.FullName;
            payload.UserName = user.UserName ?? string.Empty;
            payload.Roles = [.. roles];
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, payload.UserId)
        };

        if (!string.IsNullOrWhiteSpace(payload.Name))
        {
            claims.Add(new Claim(ClaimTypes.Name, payload.Name));
        }

        if (!string.IsNullOrWhiteSpace(payload.UserName))
        {
            claims.Add(new Claim("username", payload.UserName));
        }

        claims.AddRange(payload.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
