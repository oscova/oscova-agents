using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
[IgnoreAntiforgeryToken]
public sealed class NotificationsController(
    UserServiceManager userServiceManager,
    INotificationInbox inbox,
    IAgentCatalog agentCatalog)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NotificationEntry>>> GetAsync(CancellationToken ct)
    {
        var labels = UserLabelMap.Create(await GetUserScopeAsync(ct), agentCatalog);
        IEnumerable<NotificationEntry> entries = await inbox.GetForUserAsync(GetUserId(), ct);
        if (User.IsInRole("Admin"))
        {
            entries = entries.Concat(await inbox.GetSystemForUserAsync(GetUserId(), ct));
        }

        // Inbox text stores provider and MCP server ids; the reader sees its own labels.
        return Ok(entries
            .Select(entry => entry with { Title = labels.ReplaceIds(entry.Title), Body = labels.ReplaceIds(entry.Body) })
            .OrderByDescending(entry => entry.CreatedAt)
            .ToList());
    }

    [HttpPost("mark-all-read")]
    public async Task<IActionResult> MarkAllReadAsync(CancellationToken ct)
    {
        await inbox.MarkAllReadAsync(GetUserId(), includeSystem: User.IsInRole("Admin"), ct);
        return NoContent();
    }

    [HttpPost("mark-read/{entryId}")]
    public async Task<IActionResult> MarkReadAsync(string entryId, [FromQuery] bool system, CancellationToken ct)
    {
        if (system && !User.IsInRole("Admin"))
        {
            return Forbid();
        }

        await inbox.MarkReadAsync(GetUserId(), entryId, system, ct);
        return NoContent();
    }

    [HttpPost("clear")]
    public async Task<IActionResult> ClearAsync(CancellationToken ct)
    {
        await inbox.ClearAsync(GetUserId(), includeSystem: User.IsInRole("Admin"), ct);
        return NoContent();
    }
}