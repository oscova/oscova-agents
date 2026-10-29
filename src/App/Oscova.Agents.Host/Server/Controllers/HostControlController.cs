using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.HostControl;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Server.Security;
using Oscova.Agents.Host.Server.Hosting;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Infrastructure.Plugins;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Exposes host lifecycle and status endpoints for authenticated CLI operators.
/// </summary>
[ApiController]
[Route("api/host")]
[IgnoreAntiforgeryToken]
public sealed class HostControlController(
    IHostApplicationLifetime lifetime,
    WebHostRuntimeState runtimeState,
    PluginActivationSettings pluginSettings,
    PluginStartupState pluginStartupState,
    NetworkSettings networkSettings,
    NetworkUrlService networkUrlService,
    HostProcessRelauncher relauncher,
    RuntimeVersionService runtimeVersionService,
    ApplicationUpdateHostOptions updateOptions,
    ILogger<HostControlController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("version")]
    public ActionResult<HostVersionResponse> Version()
    {
        var versions = runtimeVersionService.GetSnapshot();
        return Ok(new HostVersionResponse
        {
            Version = versions.Host.DisplayVersion,
            FrameworkVersion = versions.Framework.DisplayVersion,
            ApiCompatibility = "1",
            ToolPackageVersion = updateOptions.ToolInstallation?.PackageVersion
        });
    }

    [Authorize(Policy = AgentsServerAuthenticationDefaults.HostStatusPolicy)]
    [HttpGet("status")]
    public async Task<ActionResult<HostStatusResponse>> Status()
    {
        try
        {
            await networkSettings.LoadAsync();
            var activeUrls = runtimeState.ActiveUrls.ToArray();
            var pluginRestartRequired = !pluginStartupState.EnabledPluginIds.SetEquals(pluginSettings.EnabledPluginIds);
            var networkRestartRequired = networkUrlService.RequiresRestart(networkSettings.CreateSnapshot(), activeUrls);

            return Ok(new HostStatusResponse
            {
                Pid = Environment.ProcessId,
                Urls = activeUrls,
                StartedAtUtc = runtimeState.StartedAtUtc,
                CurrentUserName = User.Identity?.Name ?? string.Empty,
                RestartRequired = pluginRestartRequired || networkRestartRequired
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load host status.");
            return Problem("An unexpected error occurred while loading host status.");
        }
    }

    [Authorize(Policy = AgentsServerAuthenticationDefaults.HostControlPolicy)]
    [HttpPost("shutdown")]
    public IActionResult Shutdown()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            lifetime.StopApplication();
        });

        return NoContent();
    }

    [Authorize(Policy = AgentsServerAuthenticationDefaults.HostControlPolicy)]
    [HttpPost("restart")]
    public IActionResult Restart()
    {
        try
        {
            relauncher.StartReplacement();
        }
        catch (Exception ex)
        {
            return Problem(ex.Message);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            lifetime.StopApplication();
        });

        return Accepted();
    }
}
