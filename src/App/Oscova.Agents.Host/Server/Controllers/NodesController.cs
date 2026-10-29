using Oscova.Agents.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Host.Server.Nodes;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Manages the authenticated account's execution devices.</summary>
[ApiController]
[Authorize]
[Route("api/nodes")]
[IgnoreAntiforgeryToken]
public sealed class NodesController(UserServiceManager users, NodeCredentialStore credentials,
    IUserPermissionService permissions,
    NodeBroker broker, ILogger<NodesController> logger, NodePluginDeploymentService plugins) : UserScopeControllerBase(users)
{
    /// <summary>Returns only owned plugin readiness, or aggregate readiness for the operator.</summary>
    [HttpGet("plugins")]
    public ActionResult<IReadOnlyList<NodePluginStatus>> PluginStatus() =>
        Ok(plugins.GetStatus(GetUserId(), User.IsInRole("Admin")));

    /// <summary>Retries failed package preparation without changing account or device permissions.</summary>
    [Authorize(Roles = "Admin")]
    [HttpPost("plugins/{pluginId}/retry")]
    public IActionResult RetryPlugin(string pluginId)
    {
        plugins.Retry(pluginId);
        return Accepted();
    }

    /// <summary>Lists enrolled devices with their current connection state.</summary>
    [HttpGet]
    public async Task<ActionResult<NodeCatalog>> List(CancellationToken ct)
    {
        var userId = GetUserId();
        var nodes = await credentials.ListAsync(userId, ct).ConfigureAwait(false);
        var summaries = nodes.Select(node =>
        {
            var hello = broker.GetConnected(userId, node.Id);
            return new NodeSummary(node, hello is not null,
                hello?.Capabilities.ToDictionary(capability => capability.Id, capability => capability.Version,
                    StringComparer.Ordinal) ?? new Dictionary<string, int>());
        }).ToArray();
        return Ok(new NodeCatalog(summaries, await permissions.GetGrantedAsync(userId, ct).ConfigureAwait(false),
            await permissions.IsOperatorAsync(userId, ct).ConfigureAwait(false)));
    }

    /// <summary>Issues a separately revocable credential for a named execution device.</summary>
    [HttpPost]
    public async Task<ActionResult<NodeEnrollment>> Enroll(EnrollNodeRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await credentials.EnrollAsync(GetUserId(), request.Name, ct).ConfigureAwait(false)); }
        catch (ArgumentException ex) { return Problem(statusCode: 400, detail: ex.Message); }
    }

    /// <summary>Revokes an owned credential and disconnects its active process.</summary>
    [HttpDelete("{nodeId}")]
    public async Task<IActionResult> Revoke(string nodeId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await credentials.RevokeAsync(userId, nodeId, ct).ConfigureAwait(false)) { return NotFound(); }

        broker.Revoke(userId, nodeId);
        logger.LogInformation("User {UserId} revoked execution node {NodeId}.", userId, nodeId);
        return NoContent();
    }
}
