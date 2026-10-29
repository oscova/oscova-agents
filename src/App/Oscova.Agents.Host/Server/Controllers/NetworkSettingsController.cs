using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Network;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Server.Hosting;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Core.Auth;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/network-settings")]
[GovernedConfigurationException]
public sealed class NetworkSettingsController(
    NetworkSettings networkSettings,
    ExecutionSettingsService executionSettings,
    WebHostRuntimeState webHostRuntimeState,
    NetworkUrlService networkUrlService,
    ILogger<NetworkSettingsController> logger) : ControllerBase
{
    private readonly NetworkSettings _networkSettings = networkSettings;
    private readonly WebHostRuntimeState _webHostRuntimeState = webHostRuntimeState;
    private readonly NetworkUrlService _networkUrlService = networkUrlService;
    private readonly ILogger<NetworkSettingsController> _logger = logger;

    /// <summary>
    /// Loads persisted network settings with the host URLs selected at startup.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<NetworkSettingsSummary>> Get()
    {
        try
        {
            await _networkSettings.LoadAsync();
            var snapshot = _networkSettings.CreateSnapshot();
            var activeUrls = _webHostRuntimeState.ActiveUrls.ToArray();
            var listenerPort = _networkUrlService.GetListenerPort(snapshot);

            return Ok(new NetworkSettingsSummary
            {
                EnableNetworkAccess = snapshot.EnableNetworkAccess,
                AllowPrivateNetworkFetch = snapshot.AllowPrivateNetworkFetch,
                AllowSandboxNetworkAccess = snapshot.AllowSandboxNetworkAccess,
                Port = snapshot.Port,
                PublicAppUrlMode = snapshot.PublicAppUrlMode,
                CustomPublicAppUrl = snapshot.CustomPublicAppUrl,
                ActiveUrls = activeUrls,
                IsNetworkAccessActive = _networkUrlService.IsNetworkAccessActive(activeUrls),
                ActivePort = _networkUrlService.GetPrimaryActivePort(activeUrls),
                LocalUrl = _networkUrlService.GetLocalHostUrl(listenerPort),
                AutoNetworkUrl = _networkUrlService.GetNetworkUrl(listenerPort),
                EffectivePublicAppUrl = _networkUrlService.GetEffectivePublicAppUrl(snapshot),
                RestartRequired = _networkUrlService.RequiresRestart(snapshot, activeUrls)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load network settings.");
            return Problem("An unexpected error occurred while loading network settings.");
        }
    }

    /// <summary>
    /// Saves Admin-controlled listener intent and public app URL settings.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Save([FromBody] UpdateNetworkSettingsRequest request, CancellationToken ct)
    {
        if (request.Port is < 1 or > 65535)
        {
            return BadRequest("Port must be between 1 and 65535.");
        }

        if (request.PublicAppUrlMode == PublicAppUrlMode.Custom &&
            !Uri.TryCreate(request.CustomPublicAppUrl, UriKind.Absolute, out _))
        {
            return BadRequest("Custom public app URL must be an absolute URL.");
        }

        try
        {
            await _networkSettings.LoadAsync();
            await executionSettings.SaveNetworkAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, new NetworkSettings.Snapshot
            {
                EnableNetworkAccess = request.EnableNetworkAccess,
                AllowPrivateNetworkFetch = request.AllowPrivateNetworkFetch,
                AllowSandboxNetworkAccess = request.AllowSandboxNetworkAccess,
                Port = request.Port,
                PublicAppUrlMode = request.PublicAppUrlMode,
                CustomPublicAppUrl = request.CustomPublicAppUrl
            }, request.ConfirmInterruptions, ct);
            return NoContent();
        }
        catch (Exception ex) when (ex is not ExecutionPermissionRegistry.ConfirmationRequiredException and not UnauthorizedAccessException and not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to save network settings.");
            return Problem("An unexpected error occurred while saving network settings.");
        }
    }
}
