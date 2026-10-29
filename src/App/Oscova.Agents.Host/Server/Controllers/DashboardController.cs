using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Host.Server.Services;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/dashboard")]
[IgnoreAntiforgeryToken]
public sealed class DashboardController(
    DashboardSnapshotCache cache,
    IDashboardMetricsService metrics,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    [HttpGet]
    public async Task<ActionResult<DashboardSnapshot>> Get(CancellationToken ct)
    {
        var currentUserId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            return Unauthorized();
        }

        var snapshot = cache.TryGet(currentUserId);
        if (snapshot is null)
        {
            snapshot = await metrics.BuildAsync(currentUserId, DateTimeOffset.UtcNow.Subtract(Window), isAdminView: false, ct);
            cache.Set(currentUserId, snapshot);
        }

        if (User.IsInRole("Admin"))
        {
            var overlay = cache.AdminOverlay;
            snapshot = snapshot with
            {
                IsAdminView = true,
                UserCount = overlay?.UserCount,
                UsageByUser = overlay?.UsageByUser,
                SystemWideUsage = overlay?.SystemWideUsage
            };
        }

        return Ok(snapshot);
    }
}
