using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.ApiContracts.Skills.Registry;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Admin endpoint for the system-scoped GitHub Personal Access Token used by the skill registry.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/github-skills-credential")]
public sealed class GitHubSkillsCredentialController(
    ISystemCredentialService systemCredentialService,
    ILogger<GitHubSkillsCredentialController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GitHubSkillsCredentialStatusContract>> Get(CancellationToken cancellationToken)
    {
        var hasToken = await systemCredentialService.HasValueAsync(
            GitHubSkillsCredential.Key,
            GitHubSkillsCredential.TokenFieldName,
            cancellationToken);

        return Ok(new GitHubSkillsCredentialStatusContract { IsConfigured = hasToken });
    }

    [HttpPut]
    public async Task<IActionResult> Save(
        [FromBody] SaveGitHubSkillsCredentialContract request,
        CancellationToken cancellationToken)
    {
        var token = request.Token?.Trim();

        // Blank input preserves an existing secret so the masked editor never clears it accidentally.
        if (string.IsNullOrEmpty(token))
        {
            var hasExistingToken = await systemCredentialService.HasValueAsync(
                GitHubSkillsCredential.Key,
                GitHubSkillsCredential.TokenFieldName,
                cancellationToken);

            if (!hasExistingToken)
            {
                return BadRequest(new ErrorResult { Message = "Enter a GitHub personal access token before saving." });
            }

            return NoContent();
        }

        try
        {
            var protectionUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (protectionUserId is null)
            {
                return Unauthorized();
            }

            await systemCredentialService.SaveAsync(
                protectionUserId,
                new CredentialWrite
                {
                    CredentialKey = GitHubSkillsCredential.Key,
                    Alias = "GitHub Skill Search",
                    Description = "Personal access token used to raise the GitHub API rate limit for skill discovery.",
                    IsManaged = false,
                    Fields =
                    [
                        new CredentialFieldWrite
                        {
                            Name = GitHubSkillsCredential.TokenFieldName,
                            IsSecret = true,
                            Source = CredentialFieldSource.Stored,
                            Value = token
                        }
                    ]
                },
                cancellationToken);

            return NoContent();
        }
        catch (ConfidentialDataLockedException)
        {
            return StatusCode(
                StatusCodes.Status423Locked,
                "Create or unlock your Vault Password before saving stored secrets.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save GitHub skills credential.");
            return Problem("An unexpected error occurred while saving the credential.");
        }
    }

    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await systemCredentialService.GetByKeyAsync(GitHubSkillsCredential.Key, cancellationToken);
            if (existing is not null)
            {
                await systemCredentialService.DeleteAsync(GitHubSkillsCredential.Key, cancellationToken);
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear GitHub skills credential.");
            return Problem("An unexpected error occurred while clearing the credential.");
        }
    }
}
