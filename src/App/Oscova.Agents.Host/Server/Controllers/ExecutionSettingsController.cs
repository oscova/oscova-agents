using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Execution;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Execution;
using Oscova.Agents.Infrastructure.Skills;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Exposes operator-only execution selection and device prerequisite checking.</summary>
[ApiController, Authorize(Roles = "Admin"), GovernedConfigurationException]
[Route("api/execution-settings")]
public sealed class ExecutionSettingsController(ExecutionSettings settings, ExecutionSettingsService service) : ControllerBase
{
    /// <summary>Returns the installation's saved execution provider.</summary>
    [HttpGet]
    public ActionResult<ExecutionSettingsRequest> Get() => Ok(new ExecutionSettingsRequest(settings.ProviderId));

    /// <summary>Checks prerequisites on this machine without persisting the requested provider.</summary>
    [HttpPost("check")]
    public Task<ExecutionAvailabilityResult> Check(ExecutionSettingsRequest request, CancellationToken ct) =>
        service.CheckAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.ProviderId, ct);

    /// <summary>Saves an available provider after any required interruption confirmation.</summary>
    [HttpPut]
    public async Task<IActionResult> Save(ExecutionSettingsRequest request, CancellationToken ct)
    {
        await service.SaveAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, request.ProviderId, request.ConfirmInterruptions, ct);
        return NoContent();
    }
}
