using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Oscova.Agents.Infrastructure.Auth;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>Accepts enrolled-device credentials only on the node transport's authentication scheme.</summary>
public sealed class NodeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    NodeCredentialStore credentials)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OscovaNode";

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var identity = await credentials.AuthenticateAsync(header[7..].Trim(), Context.RequestAborted)
            .ConfigureAwait(false);
        if (identity is null) { return AuthenticateResult.Fail("The node credential is invalid or revoked."); }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, identity.UserId),
            new Claim(NodeCredentialStore.NodeIdClaim, identity.NodeId)
        ], Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
