using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.CliAuth;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Host.Server.Security;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Provides device-code authentication endpoints for the Oscova CLI.
/// </summary>
[ApiController]
[Route("api/auth/cli")]
[IgnoreAntiforgeryToken]
public sealed class CliAuthController(
    ICliDeviceCodeStore grants,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("device/start")]
    public ActionResult<StartCliDeviceLoginResponse> Start([FromBody] StartCliDeviceLoginRequest request)
    {
        var baseUri = new Uri($"{Request.Scheme}://{Request.Host}{Request.PathBase}/");
        var grant = grants.Create(request.Client, request.Machine, baseUri);
        var expiresIn = Math.Max(0, (int)(grant.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds);

        return Ok(new StartCliDeviceLoginResponse
        {
            DeviceCode = grant.DeviceCode,
            UserCode = grant.UserCode,
            VerificationUri = grant.VerificationUri,
            IntervalSeconds = 2,
            ExpiresInSeconds = expiresIn
        });
    }

    [AllowAnonymous]
    [HttpPost("device/poll")]
    public ActionResult<PollCliDeviceLoginResponse> Poll([FromBody] PollCliDeviceLoginRequest request)
    {
        var grant = grants.FindByDeviceCode(request.DeviceCode);
        if (grant?.IsExpired != false)
        {
            return Ok(new PollCliDeviceLoginResponse { Status = "expired" });
        }

        if (grant.Status == CliDeviceGrantStatus.Denied)
        {
            return Ok(new PollCliDeviceLoginResponse { Status = "denied" });
        }

        if (grant.Status != CliDeviceGrantStatus.Approved)
        {
            return Ok(new PollCliDeviceLoginResponse { Status = "pending" });
        }

        if (!grants.TryRemove(request.DeviceCode))
        {
            return Ok(new PollCliDeviceLoginResponse { Status = "expired" });
        }

        return Ok(new PollCliDeviceLoginResponse
        {
            Status = "approved",
            Token = grant.Token,
            IdentityUserId = grant.IdentityUserId,
            IdentityUserName = grant.IdentityUserName,
            IssuedAtUtc = grant.IssuedAtUtc,
            ExpiresAtUtc = null
        });
    }

    [Authorize(AuthenticationSchemes = AgentsServerAuthenticationDefaults.BlazorBearerScheme)]
    [HttpPost("revoke")]
    public async Task<IActionResult> Revoke(CancellationToken ct)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        user.CliTokenVersion++;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return Problem("CLI token could not be revoked.");
        }

        return NoContent();
    }
}
