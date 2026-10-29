using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>
/// Authenticates the runtime control token used by the same OS user over loopback.
/// </summary>
public sealed class LocalControlAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LocalControlTokenProvider tokenProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string HeaderName = "X-Oscova-Control-Token";
    public const string LocalControlClaim = "oscova:local-control";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // The runtime token is process-control trust only; never accept it from off-box callers.
        var remoteIp = Context.Connection.RemoteIpAddress;
        if (remoteIp is null || !IPAddress.IsLoopback(remoteIp))
        {
            return Task.FromResult(AuthenticateResult.Fail("Local control is only permitted over loopback."));
        }

        var presented = headerValues.ToString();
        var expected = tokenProvider.Token;
        if (string.IsNullOrEmpty(presented) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented),
                Encoding.UTF8.GetBytes(expected)))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid local control token."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(LocalControlClaim, "true")],
            Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
