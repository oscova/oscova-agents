using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Updates;
using Oscova.Agents.Host.Server.Security;
using Oscova.Agents.Host.Services;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Exposes operator-only management of the harness computer's local Avatar installation.</summary>
[ApiController]
[Authorize(AuthenticationSchemes = AgentsServerAuthenticationDefaults.SmartScheme, Roles = "Admin")]
[Route("api/avatar-update")]
public sealed class AvatarUpdateController(AvatarUpdateService service, UpdateDiscoveryService discovery) : ControllerBase
{
    /// <summary>Returns current eligibility and server-owned progress, refreshing discovery on explicit request.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<AvatarUpdateStatus>> Status([FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (refresh) { await discovery.CheckAsync(true, cancellationToken).ConfigureAwait(false); }

        return Ok(await service.GetStatusAsync(false, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Accepts an explicitly confirmed release without tying installation to this HTTP connection.</summary>
    [HttpPost("start")]
    [Authorize(Policy = AgentsServerAuthenticationDefaults.ApplicationUpdatePolicy)]
    [IgnoreAntiforgeryToken]
    public async Task<ActionResult<AvatarUpdateStatus>> Start(StartAvatarUpdateRequest request, CancellationToken cancellationToken)
    {
        try { return Accepted(await service.StartAsync(request, cancellationToken).ConfigureAwait(false)); }
        catch (ApplicationUpdateConflictException ex)
        {
            return Conflict(new ProblemDetails { Title = "Avatar operation unavailable", Detail = ex.Message });
        }
        catch (ApplicationUpdateException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Avatar operation unavailable", Detail = ex.Message });
        }
    }
}
