using Microsoft.AspNetCore.Identity;
using Oscova.Agents.Infrastructure.Auth;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>
/// Issues non-expiring CLI bearer tokens for Identity users.
/// </summary>
public sealed class CliAccessTokenService(
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    BlazorAccessTokenService tokenService)
{
    public async Task<CliAccessTokenResult> CreateTokenAsync(ApplicationUser user)
    {
        var principal = await claimsFactory.CreateAsync(user).ConfigureAwait(false);
        var token = tokenService.CreateToken(principal, expiresAt: null, cliTokenVersion: user.CliTokenVersion);
        return new CliAccessTokenResult(token.Token, user.Id, user.UserName ?? string.Empty, DateTimeOffset.UtcNow);
    }
}

public sealed record CliAccessTokenResult(string Token, string UserId, string UserName, DateTimeOffset IssuedAtUtc);