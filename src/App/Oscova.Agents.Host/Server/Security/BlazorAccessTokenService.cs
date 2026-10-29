using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Oscova.Agents.Host.Server.Security;

public sealed class BlazorAccessTokenService(IDataProtectionProvider dataProtectionProvider)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Oscova.Agents.Host.BlazorAccessToken.v1");

    public AccessTokenResult CreateToken(ClaimsPrincipal user)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(TokenLifetime);
        return CreateToken(user, expiresAt, cliTokenVersion: null);
    }

    /// <summary>
    /// Creates a protected bearer token for either short-lived browser API calls or revocable CLI access.
    /// </summary>
    public AccessTokenResult CreateToken(ClaimsPrincipal user, DateTimeOffset? expiresAt, int? cliTokenVersion)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("Authenticated user is missing a user identifier.");
        }

        var payload = new AccessTokenPayload
        {
            UserId = userId,
            Name = user.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            UserName = user.Identity?.Name ?? string.Empty,
            Roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray(),
            ExpiresAtUnixTimeSeconds = expiresAt?.ToUnixTimeSeconds() ?? long.MaxValue,
            CliTokenVersion = cliTokenVersion
        };

        var token = _protector.Protect(JsonSerializer.Serialize(payload));
        return new AccessTokenResult(token, expiresAt);
    }

    public AccessTokenPayload? ValidateToken(string token)
    {
        try
        {
            var json = _protector.Unprotect(token);
            var payload = JsonSerializer.Deserialize<AccessTokenPayload>(json);
            if (payload is null)
            {
                return null;
            }

            if (payload.ExpiresAtUnixTimeSeconds != long.MaxValue &&
                payload.ExpiresAtUnixTimeSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return null;
            }

            return payload;
        }
        catch
        {
            return null;
        }
    }

    public sealed record AccessTokenResult(string Token, DateTimeOffset? ExpiresAt);

    public sealed class AccessTokenPayload
    {
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string[] Roles { get; set; } = [];
        public long ExpiresAtUnixTimeSeconds { get; set; }
        public int? CliTokenVersion { get; set; }
    }
}
