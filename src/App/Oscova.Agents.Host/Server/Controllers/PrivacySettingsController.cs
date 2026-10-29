using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Privacy;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Llm;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/settings/privacy")]
[IgnoreAntiforgeryToken]
public sealed class PrivacySettingsController(
    UserServiceManager userServiceManager,
    IModelProviderCatalog modelProviderCatalog,
    ILogger<PrivacySettingsController> logger)
    : UserScopeControllerBase(userServiceManager)
{
    /// <summary>
    /// Loads the current user's privacy posture and read-only offline-mode effects.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PrivacySettingsSummary>> Get(CancellationToken ct)
    {
        try
        {
            var userScope = await GetUserScopeAsync(ct);
            var settings = userScope.PrivacySettings;
            await settings.LoadAsync();

            return Ok(CreateSummary(userScope, modelProviderCatalog));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load privacy settings.");
            return Problem("An unexpected error occurred while loading privacy settings.");
        }
    }

    /// <summary>
    /// Saves the current user's privacy posture and invalidates cached agents when offline mode changes.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Save([FromBody] UpdatePrivacySettingsRequest? request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Privacy settings request is required.");
        }

        if (!PrivacySettings.AllowedIdleLogoutMinutes.Contains(request.IdleLogoutMinutes))
        {
            return BadRequest("Automatic sign-out must be Off, 5, 15, or 30 minutes.");
        }

        try
        {
            var userScope = await GetUserScopeAsync(ct);
            var settings = userScope.PrivacySettings;
            await settings.LoadAsync();
            var localOnlyChanged = settings.IsLocalOnly != request.IsLocalOnly;
            settings.ApplyFrom(new PrivacySettings.Snapshot
            {
                IsLocalOnly = request.IsLocalOnly,
                IdleLogoutMinutes = request.IdleLogoutMinutes
            });

            await settings.SaveAsync();
            if (localOnlyChanged)
            {
                userScope.AppService.InvalidateAllCachedAgents();
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save privacy settings.");
            return Problem("An unexpected error occurred while saving privacy settings.");
        }
    }

    private static PrivacySettingsSummary CreateSummary(UserServiceScope userScope, IModelProviderCatalog modelProviderCatalog)
    {
        return new PrivacySettingsSummary
        {
            IsLocalOnly = userScope.PrivacySettings.IsLocalOnly,
            IdleLogoutMinutes = userScope.PrivacySettings.IdleLogoutMinutes,
            DisabledNetworkToolCount = userScope.CrewService.Tools
                .Count(tool => userScope.CrewService.Tools.GetNetworkScope(tool.Name) == NetworkScope.Egress),
            CloudProviderNames = [.. userScope.ModelProviderSettings.ModelProviderConfigs
                .Where(config =>
                {
                    var provider = modelProviderCatalog.Find(config.Type);
                    return provider is null ||
                        EgressClassifier.Classify(
                            config.Type,
                            config.BaseUrl,
                            provider.DefaultEndpoint,
                            config.IsLocalEndpoint,
                            provider.DefaultIsLocalEndpoint) == EgressClass.Cloud;
                })
                .Select(config => config.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)]
        };
    }
}
