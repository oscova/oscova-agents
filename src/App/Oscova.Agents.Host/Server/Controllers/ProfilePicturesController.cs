using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Images;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Definitions;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Serves and accepts uploads for user and agent profile pictures.
/// Backed by <see cref="IProfilePictureService"/>, which seeds missing pictures before reads.
/// </summary>
[ApiController]
[Authorize]
[Route("api/profile-pictures")]
[IgnoreAntiforgeryToken]
public sealed class ProfilePicturesController(
    UserServiceManager userServiceManager,
    IProfilePictureService profilePictures,
    IAgentDefinitionRepository agentDefinitionStore)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpGet("seeds")]
    public IActionResult GetSeeds()
    {
        return Ok(new ImageSeedList { Seeds = profilePictures.GetAvailableSeedNames() });
    }

    [HttpGet("seeds/{seedName}")]
    public IActionResult GetSeedPicture(string seedName)
    {
        try
        {
            var result = profilePictures.OpenSeedPicture(seedName);
            return Stream(result);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("user")]
    public IActionResult GetUserPicture()
    {
        var result = profilePictures.OpenUserPicture(GetUserId());
        return Stream(result);
    }

    [HttpGet("agent/{agentId}")]
    public async Task<IActionResult> GetAgentPicture(string agentId, CancellationToken ct)
    {
        // Resolve through the store so non-visible agents return 404 rather than leaking existence.
        var userId = GetUserId();
        var agent = await agentDefinitionStore.GetAgentAsync(userId, agentId, ct);
        if (agent is null)
        {
            return NotFound();
        }

        var result = profilePictures.OpenAgentPicture(agentId);
        return Stream(result);
    }

    [HttpPost("user")]
    [RequestSizeLimit(ProfilePictureContent.MaxRequestSizeBytes)]
    public async Task<IActionResult> UploadUserPicture(IFormFile file, CancellationToken ct)
    {
        if (!ImageUploadValidation.TryValidate(file, ProfilePictureContent.MaxUploadSizeBytes, "Profile pictures", out var error))
        {
            return BadRequest(new { message = error });
        }

        await using var stream = file.OpenReadStream();
        await profilePictures.WriteUserPictureAsync(GetUserId(), stream, ct);
        return NoContent();
    }

    [HttpPost("user/seed/{seedName}")]
    public async Task<IActionResult> ApplyUserSeedPicture(string seedName, CancellationToken ct)
    {
        try
        {
            await profilePictures.ApplyUserSeedAsync(GetUserId(), seedName, ct);
            return NoContent();
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("agent/{agentId}")]
    [RequestSizeLimit(ProfilePictureContent.MaxRequestSizeBytes)]
    public async Task<IActionResult> UploadAgentPicture(string agentId, IFormFile file, CancellationToken ct)
    {
        if (!ImageUploadValidation.TryValidate(file, ProfilePictureContent.MaxUploadSizeBytes, "Profile pictures", out var error))
        {
            return BadRequest(new { message = error });
        }

        var userScope = await GetUserScopeAsync(ct);
        var agent = await agentDefinitionStore.GetAgentAsync(userScope.UserId, agentId, ct);
        if (agent is null)
        {
            return NotFound();
        }

        // Match shared-agent definition editing: owners and privileged shared-agent editors may replace pictures.
        if (!CanUploadAgentPicture(agent, userScope))
        {
            return Forbid();
        }

        await using var stream = file.OpenReadStream();
        await profilePictures.WriteAgentPictureAsync(agentId, stream, ct);
        return NoContent();
    }

    [HttpPost("agent/{agentId}/seed/{seedName}")]
    public async Task<IActionResult> ApplyAgentSeedPicture(string agentId, string seedName, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var agent = await agentDefinitionStore.GetAgentAsync(userScope.UserId, agentId, ct);
        if (agent is null)
        {
            return NotFound();
        }

        if (!CanUploadAgentPicture(agent, userScope))
        {
            return Forbid();
        }

        try
        {
            await profilePictures.ApplyAgentSeedAsync(agentId, seedName, ct);
            return NoContent();
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    private static bool CanUploadAgentPicture(AgentDefinition agent, UserServiceScope userScope)
    {
        return !agent.IsShared ||
               (!string.IsNullOrWhiteSpace(agent.OwnerUserId) &&
                string.Equals(agent.OwnerUserId, userScope.UserId, StringComparison.OrdinalIgnoreCase)) ||
               userScope.IsInRole("Admin") ||
               userScope.IsInRole("Moderator");
    }

    private IActionResult Stream(ImageReadResult result)
    {
        // Disable caching so the UI picks up replaced pictures without needing ETags or query busting.
        Response.Headers.CacheControl = "no-store";
        return File(result.Stream, result.ContentType);
    }
}
