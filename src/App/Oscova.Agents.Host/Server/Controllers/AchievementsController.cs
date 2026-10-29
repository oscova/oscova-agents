using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Achievements;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Host.Achievements;
using Oscova.Agents.Host.Server.Services;
using Oscova.Agents.Infrastructure.Auth;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Returns the signed-in user's achievement wall derived from dashboard metrics.
/// </summary>
[ApiController]
[Authorize]
[Route("api/achievements")]
[IgnoreAntiforgeryToken]
public sealed class AchievementsController(
    DashboardSnapshotCache cache,
    IDashboardMetricsService metrics,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets the current user's derived achievement progress.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<AchievementSummary>> Get(CancellationToken ct)
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

        return Ok(AchievementEvaluator.Evaluate(snapshot));
    }
}
