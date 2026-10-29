using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.ApiContracts.Plugins.Registry;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Admin-only HTTP surface for finding and installing NuGet-backed Oscova plugins.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/plugin-registry")]
public sealed class PluginRegistryController(
    IPluginRegistryService registry,
    IPluginInstallService installer,
    PluginActivationSettings activationSettings,
    IStringLocalizer<SettingsText> localizer,
    ILogger<PluginRegistryController> logger) : ControllerBase
{
    [HttpGet("search")]
    public async Task<ActionResult<PluginRegistryCatalogResponse>> Search(
        [FromQuery] string? q,
        CancellationToken cancellationToken)
    {
        try
        {
            var entries = await registry.SearchAsync(q ?? string.Empty, cancellationToken);
            return Ok(new PluginRegistryCatalogResponse { Entries = [.. entries] });
        }
        catch (NuGetPluginRegistryException ex)
        {
            logger.LogWarning(ex, "NuGet plugin search failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResult { Message = ex.Message });
        }
    }

    [HttpGet("metadata/{packageId}")]
    public async Task<ActionResult<PluginRegistryMetadataResponse>> GetMetadata(
        string packageId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return BadRequest(new ErrorResult { Message = "packageId is required." });
        }

        try
        {
            var entry = await registry.GetMetadataAsync(packageId, cancellationToken);
            if (entry is null)
            {
                return NotFound(new ErrorResult { Message = "The requested plugin package was not found." });
            }

            return Ok(new PluginRegistryMetadataResponse { Entry = entry });
        }
        catch (NuGetPluginRegistryException ex)
        {
            logger.LogWarning(ex, "NuGet plugin metadata lookup failed for package {PackageId}.", packageId);
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResult { Message = ex.Message });
        }
    }

    [HttpPost("install")]
    public async Task<IActionResult> Install(
        [FromBody] InstallRemotePluginRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId))
        {
            return BadRequest(new ErrorResult { Message = "packageId is required." });
        }

        try
        {
            var pluginId = await installer.InstallAsync(
                request.PackageId,
                packageVersion: null,
                expectedPluginId: null,
                cancellationToken,
                request.ReplacePluginId,
                request.ReplacePackageId);
            activationSettings.SetRemoved(pluginId, false);
            await activationSettings.SaveAsync().ConfigureAwait(false);
            return NoContent();
        }
        catch (PluginPackageReplacementRequiredException ex)
        {
            return Conflict(new PluginInstallFailure(ex.Message, PluginInstallFailure.ReplacementRequired,
                ex.PluginId, ex.PackageId));
        }
        catch (PluginInstallConflictException ex)
        {
            return Conflict(new ErrorResult { Message = ex.Message });
        }
        catch (PluginPlatformNotSupportedException)
        {
            return BadRequest(new ErrorResult
            {
                Message = localizer.GetOrDefault(SettingsTextKeys.PluginsNotAvailableOnPlatform, "This plugin is not available on this platform.")
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResult { Message = ex.Message });
        }
        catch (NuGetPluginRegistryException ex)
        {
            logger.LogWarning(ex, "Remote plugin install failed for package {PackageId}.", request.PackageId);
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResult { Message = ex.Message });
        }
    }
}
