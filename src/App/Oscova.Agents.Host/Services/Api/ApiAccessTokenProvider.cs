using Microsoft.AspNetCore.Components.Authorization;
using Oscova.Agents.Host.Server.Security;

namespace Oscova.Agents.Host.Services.Api;

public sealed class ApiAccessTokenProvider(
    AuthenticationStateProvider authenticationStateProvider,
    BlazorAccessTokenService accessTokenService)
{
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string?> GetTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(_token) && _expiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return _token;
        }

        var authState = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var user = authState.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            _token = null;
            _expiresAt = default;
            return null;
        }

        var result = accessTokenService.CreateToken(user);
        _token = result.Token;
        _expiresAt = result.ExpiresAt ?? DateTimeOffset.MaxValue;
        return _token;
    }
}
