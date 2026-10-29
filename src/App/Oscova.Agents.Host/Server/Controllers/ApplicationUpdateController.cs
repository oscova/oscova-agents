using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Updates;
using Oscova.Agents.Host.Server.Security;
using Oscova.Agents.Host.Services;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Exposes operator update checks, update console start and result acknowledgement.</summary>
[ApiController]
[Route("api/application-update")]
public sealed class ApplicationUpdateController(
    ApplicationUpdateService updateService,
    UpdateDiscoveryService discovery) : ControllerBase
{
    [Authorize(AuthenticationSchemes = AgentsServerAuthenticationDefaults.SmartScheme, Roles = "Admin")]
    [HttpGet("status")]
    public async Task<ActionResult<ApplicationUpdateStatus>> Status(
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (refresh) { await discovery.CheckAsync(true, cancellationToken).ConfigureAwait(false); }

        return Ok(await updateService.GetStatusAsync(false, cancellationToken).ConfigureAwait(false));
    }

    [Authorize(Policy = AgentsServerAuthenticationDefaults.ApplicationUpdatePolicy)]
    [IgnoreAntiforgeryToken]
    [HttpPost("start")]
    public async Task<ActionResult<ApplicationUpdateStatus>> Start(
        StartApplicationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await updateService.StartAsync(request.TargetVersion, cancellationToken).ConfigureAwait(false);
            return Accepted(result);
        }
        catch (ApplicationUpdateConflictException ex)
        {
            return Conflict(new ProblemDetails { Title = "Application update unavailable", Detail = ex.Message });
        }
        catch (ApplicationUpdateException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Application update unavailable", Detail = ex.Message });
        }
    }

    /// <summary>Retains a completed upgrade journal after the operator closes its result.</summary>
    [Authorize(AuthenticationSchemes = AgentsServerAuthenticationDefaults.SmartScheme, Roles = "Admin")]
    [HttpPost("acknowledge/{operationId}")]
    public async Task<IActionResult> Acknowledge(string operationId, CancellationToken cancellationToken)
    {
        try
        {
            await updateService.AcknowledgeAsync(operationId, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (Exception ex) when (ex is ApplicationUpdateException or InvalidOperationException)
        {
            return BadRequest(new ProblemDetails { Title = "Application update acknowledgement rejected", Detail = ex.Message });
        }
    }
}
