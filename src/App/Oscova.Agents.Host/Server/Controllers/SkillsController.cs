using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Skills;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Manages personal skills and explicit publication of operator-selected packages.</summary>
[ApiController]
[Authorize]
[Route("api/skills")]
[IgnoreAntiforgeryToken]
[GovernedConfigurationException]
public sealed class SkillsController(UserServiceManager userServiceManager,
    IUserDirectoryService directories, ISkillImportService importer, SkillImportService packages,
    SkillGovernanceService governance, IUserPermissionService permissions)
    : UserScopeControllerBase(userServiceManager)
{
    private const long MaxImportRequestSizeBytes = SkillImportLimits.MaxUploadSizeBytes + (8 * 1024L);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SkillDefinition>>> Get(CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        var shared = (await governance.GetSharedNamesAsync(scope.UserId, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isOperator = await permissions.IsOperatorAsync(scope.UserId, ct);
        var canManage = isOperator || (await permissions.GetGrantedAsync(scope.UserId, ct))
            .Contains(UserPermissionKeys.ManageCustomSkills);
        var skills = new SkillDiscoverer().Discover(directories.GetSkillsDirectoryPath(scope.UserId))
            .Select(skill => new SkillDefinition
            {
                Name = skill.Name,
                Description = skill.Description,
                IsShared = shared.Contains(skill.Name),
                IsBundled = ProductGuideSkillSeed.BundledSkillNames.Contains(skill.Name),
                CanEdit = canManage && (isOperator || !shared.Contains(skill.Name))
            }).ToList();
        if (isOperator)
        {
            // A removed private source must not hide the action for withdrawing its published copies.
            skills.AddRange(shared.Except(skills.Select(skill => skill.Name), StringComparer.OrdinalIgnoreCase)
                .Select(name => new SkillDefinition { Name = name, IsShared = true, CanEdit = false }));
        }

        return Ok(skills.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [HttpPost("import")]
    [RequestSizeLimit(MaxImportRequestSizeBytes)]
    public async Task<IActionResult> Import([FromForm] SkillImportRequest request, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        if (request.File is null || request.File.Length == 0)
        {
            return BadRequest(new ErrorResult { Message = "A skill file is required." });
        }

        await using var content = request.File.OpenReadStream();
        var result = await importer.ImportAsync(scope.UserId, request.File.FileName, content, ct);
        scope.AppService.InvalidateAllCachedAgents();
        return Ok(SkillImportResultMapper.ToImportedSkillResult(result));
    }

    [HttpDelete("{skillName}")]
    public async Task<IActionResult> Delete(string skillName, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        await governance.RequireEditableAsync(scope.UserId, skillName, ct);
        var installed = new SkillDiscoverer().Discover(directories.GetSkillsDirectoryPath(scope.UserId))
            .Any(skill => string.Equals(skill.Name, skillName, StringComparison.Ordinal));
        if (!installed) { return NotFound(); }
        // Published copies have an explicit unpublish action, independent of private operator edits.
        await scope.AppService.RemoveSkillFromAgentsAsync(skillName);
        packages.RemoveTrustedPackage(scope.UserId, skillName);
        scope.AppService.InvalidateAllCachedAgents();
        return NoContent();
    }

    [HttpPut("{skillName}/shared")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Publish(string skillName, [FromQuery] bool enabled,
        [FromQuery] bool confirmed, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        try
        {
            return Ok(await governance.PublishAsync(scope.UserId, skillName, enabled, confirmed, ct));
        }
        finally
        {
            // Refresh future discovery. The execution coordinator owns cancellation of affected work.
            foreach (var id in UserServices.ActiveUserIds)
            {
                if (UserServices.TryGetExistingScope(id, out var recipient))
                {
                    recipient.AppService.InvalidateAllCachedAgents();
                }
            }
        }
    }

    /// <summary>Accepts one skill package through the existing multipart upload.</summary>
    public sealed class SkillImportRequest
    {
        public IFormFile? File { get; set; }
    }
}
