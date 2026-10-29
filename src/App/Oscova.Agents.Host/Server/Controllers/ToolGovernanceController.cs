using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Tools;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.ToolGovernance;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Exposes per-user tool governance policy and quota usage for the settings UI.
/// </summary>
[ApiController]
[Authorize]
[Route("api/tool-governance")]
[IgnoreAntiforgeryToken]
public sealed class ToolGovernanceController(
    UserServiceManager userServiceManager,
    IToolQuotaUsageStore quotaUsageStore)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpGet]
    public async Task<ActionResult<ToolGovernanceSummary>> Get(CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        await scope.ToolGovernanceSettings.LoadAsync();
        return Ok(await BuildSummaryAsync(scope, ct));
    }

    [HttpPut("{qualifiedName}")]
    public async Task<IActionResult> Save(
        string qualifiedName,
        [FromBody] UpdateToolGovernanceRequest? request,
        CancellationToken ct)
    {
        if (request is null ||
            !string.Equals(qualifiedName, request.QualifiedName, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Tool governance request is required and must match the route tool name.");
        }

        var validation = Validate(request);
        if (validation is not null)
        {
            return validation;
        }

        var scope = await GetUserScopeAsync(ct);
        await scope.ToolGovernanceSettings.RunExclusiveAsync(async () =>
        {
            await scope.ToolGovernanceSettings.LoadAsync();
            var normalizedLimit = NormalizeLimit(request.CallLimit);
            if (request.NetworkScopeOverride is null && normalizedLimit is null)
            {
                scope.ToolGovernanceSettings.Remove(request.QualifiedName);
            }
            else
            {
                scope.ToolGovernanceSettings.Upsert(new ToolGovernanceEntry
                {
                    QualifiedName = request.QualifiedName,
                    NetworkScopeOverride = request.NetworkScopeOverride,
                    CallLimit = normalizedLimit
                });
            }

            await scope.ToolGovernanceSettings.SaveAsync();
            scope.AppService.SyncTools();
            scope.AppService.InvalidateAllCachedAgents();
        }, ct);

        return NoContent();
    }

    private async Task<ToolGovernanceSummary> BuildSummaryAsync(UserServiceScope scope, CancellationToken ct)
    {
        var tools = new List<ToolGovernanceEntrySummary>();
        var now = DateTimeOffset.UtcNow;

        foreach (var tool in scope.AppService.AllTools.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            var qualifiedName = tool.Name;
            var saved = scope.ToolGovernanceSettings.Find(qualifiedName);
            var declaredScope = scope.CrewService.Tools.GetDeclaredNetworkScope(qualifiedName);
            var effectiveScope = scope.CrewService.Tools.GetNetworkScope(qualifiedName);
            var usage = saved?.CallLimit is { IsEnabled: true } limit
                ? await quotaUsageStore.GetUsageAsync(scope.UserId, qualifiedName, limit, now, ct)
                : null;

            tools.Add(new ToolGovernanceEntrySummary
            {
                QualifiedName = qualifiedName,
                DeclaredNetworkScope = declaredScope,
                EffectiveNetworkScope = effectiveScope,
                HasNetworkScopeOverride = saved?.NetworkScopeOverride is not null,
                CallLimit = saved?.CallLimit,
                Usage = usage
            });
        }

        return new ToolGovernanceSummary { Tools = tools };
    }

    private static IActionResult? Validate(UpdateToolGovernanceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.QualifiedName))
        {
            return new BadRequestObjectResult("Tool name is required.");
        }

        if (request.NetworkScopeOverride is { } scope &&
            scope is not (NetworkScope.None or NetworkScope.Incidental or NetworkScope.Egress))
        {
            return new BadRequestObjectResult("Network scope override is not valid.");
        }

        if (request.CallLimit is { IsEnabled: true } limit)
        {
            if (limit.MaxCalls is < 1 or > 1_000_000)
            {
                return new BadRequestObjectResult("Tool call limit must be between 1 and 1000000.");
            }

            if (limit.WindowHours != 24)
            {
                return new BadRequestObjectResult("Only a 24-hour tool call window is supported.");
            }
        }

        return null;
    }

    private static ToolCallLimit? NormalizeLimit(ToolCallLimit? limit)
    {
        return limit is { IsEnabled: true }
            ? limit with { WindowHours = 24 }
            : null;
    }
}
