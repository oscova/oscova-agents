using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.ApiContracts.Skills;
using Oscova.Agents.Core.ApiContracts.Skills.Registry;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Skills.Registry;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// HTTP surface for the "Find skills online" feature: browse configured GitHub registries and install one skill.
/// </summary>
[ApiController]
[Authorize]
[Route("api/skill-registry")]
[GovernedConfigurationException]
public sealed class SkillRegistryController(
    UserServiceManager userServiceManager,
    SkillRegistrySettings settings,
    IGitHubSkillRegistryService registry,
    IGitHubSkillInstallService installer,
    IUserPermissionService permissions,
    ILogger<SkillRegistryController> logger)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpGet("catalog")]
    public async Task GetCatalog(
        [FromServices] Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        await permissions.RequireAsync(GetUserId(), Core.Auth.UserPermissionKeys.InstallApprovedSkills, cancellationToken);
        // Send headers before native acquisition so the client can await the body until canceled.
        Response.ContentType = "application/json";
        await Response.StartAsync(cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
        var response = new SkillRegistryCatalogResponse();
        foreach (var source in settings.Sources)
        {
            try
            {
                var catalog = await registry.EnumerateSkillsAsync(source, cancellationToken);
                foreach (var entry in catalog.Entries) { response.Entries.Add(entry); }

                foreach (var warning in catalog.Errors) { response.Errors.Add(warning); }
            }
            catch (GitHubRegistryException ex)
            {
                logger.LogWarning(ex, "Failed to enumerate skills from {Source}.", source.DisplayName);
                response.Errors.Add($"{source.DisplayName}: {ex.Message}");
            }
        }

        await System.Text.Json.JsonSerializer.SerializeAsync(
            Response.Body, response, jsonOptions.Value.JsonSerializerOptions, cancellationToken);
    }

    [HttpGet("description")]
    public async Task<ActionResult<string?>> GetDescription(
        [FromQuery] string owner,
        [FromQuery] string repo,
        [FromQuery] string branch,
        [FromQuery] string skillPath,
        CancellationToken cancellationToken)
    {
        await permissions.RequireAsync(GetUserId(), Core.Auth.UserPermissionKeys.InstallApprovedSkills, cancellationToken);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(skillPath))
        {
            return BadRequest(new ErrorResult { Message = "owner, repo, and skillPath are required." });
        }

        var skillName = skillPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(skillName))
        {
            return BadRequest(new ErrorResult { Message = "skillPath must include a skill folder name." });
        }

        var source = settings.FindSource(owner, repo, branch);
        if (source is null)
        {
            return BadRequest(new ErrorResult { Message = "The requested skill source is not configured." });
        }

        var entry = new RemoteSkillCatalogEntry
        {
            Source = source,
            SkillName = skillName,
            SkillPath = skillPath
        };

        var description = await registry.FetchDescriptionAsync(entry, cancellationToken);
        return Ok(description);
    }

    [HttpPost("install")]
    public async Task<ActionResult<ImportedSkillResult>> Install(
        [FromBody] InstallRemoteSkillRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Owner) ||
            string.IsNullOrWhiteSpace(request.Repo) ||
            string.IsNullOrWhiteSpace(request.SkillPath))
        {
            return BadRequest(new ErrorResult { Message = "owner, repo, and skillPath are required." });
        }

        var skillName = request.SkillPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(skillName))
        {
            return BadRequest(new ErrorResult { Message = "skillPath must include a skill folder name." });
        }

        var source = settings.FindSource(request.Owner, request.Repo, request.Branch);
        if (source is null)
        {
            return BadRequest(new ErrorResult { Message = "The requested skill source is not configured." });
        }

        var userScope = await GetUserScopeAsync(cancellationToken);

        try
        {
            var result = await installer.InstallAsync(
                userScope.UserId,
                source,
                skillName,
                request.SkillPath,
                progress: null,
                cancellationToken);

            // Imported skills only show up after cached agents rebuild with the refreshed skills directory.
            userScope.AppService.CrewService.InvalidateAllAgents();
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorResult { Message = ex.Message });
        }
        catch (SkillImportConflictException ex)
        {
            return Conflict(new ErrorResult { Message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResult { Message = ex.Message });
        }
        catch (GitHubRegistryException ex)
        {
            logger.LogWarning(ex, "Remote skill install failed for {Source}/{Path}.", source.DisplayName, request.SkillPath);
            return StatusCode(StatusCodes.Status502BadGateway, new ErrorResult { Message = ex.Message });
        }
    }
}

/// <summary>
/// Admin-only settings surface for the list of GitHub repositories that act as skill registries.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/skill-registry-settings")]
public sealed class SkillRegistrySettingsController(
    SkillRegistrySettings settings,
    ILogger<SkillRegistrySettingsController> logger) : ControllerBase
{
    [HttpGet]
    public ActionResult<SkillRegistrySettings.Snapshot> Get() => Ok(settings.CreateSnapshot());

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] UpdateSkillRegistrySettingsRequest request)
    {
        try
        {
            // The operator's acquisition choice and sources share one persisted settings domain.
            settings.SearchMethod = request.SearchMethod;
            settings.ReplaceSources(request.Sources ?? []);
            await settings.SaveAsync();
            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save skill registry settings.");
            return Problem("An unexpected error occurred while saving skill registry settings.");
        }
    }
}
