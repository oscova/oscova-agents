using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Memory;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/chat-memory")]
[IgnoreAntiforgeryToken]
public sealed class ChatMemoryController(UserServiceManager userServiceManager)
    : UserScopeControllerBase(userServiceManager)
{
    /// <summary>
    /// Returns one page of stored chat-memory chunks for the authenticated user.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ChatMemoryPage>> GetPage(
        [FromQuery] string? agentId,
        [FromQuery] string? threadId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var userScope = await GetUserScopeAsync(ct);

        // This controller method is the HTTP handoff from the shell dialog to the per-user memory service.
        var memoryPage = await userScope.ChatMemoryService.GetPageAsync(
            GetUserId(),
            agentId,
            threadId,
            page,
            pageSize,
            ct);

        return Ok(memoryPage);
    }
}
