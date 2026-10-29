using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.Channels;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Manages active plugin channels for the authenticated owner.</summary>
[ApiController]
[Authorize]
[Route("api/channels")]
[IgnoreAntiforgeryToken]
public sealed class ChannelsController(UserServiceManager users, IChannelCatalog catalog, IChannelManager manager,
    ILogger<ChannelsController> logger) : UserScopeControllerBase(users)
{
    /// <summary>Returns active channel declarations without credentials.</summary>
    [HttpGet]
    public IActionResult GetCatalog() => Ok(catalog.Channels);

    /// <summary>Returns one owner's saved configuration and current status.</summary>
    [HttpGet("{channelId}")]
    public Task<IActionResult> Get(string channelId, CancellationToken ct) =>
        ExecuteAsync(channelId, async () => Ok(await manager.GetAsync(GetUserId(), channelId, ct)));

    /// <summary>Applies independently saved plugin configuration to an already running connection.</summary>
    [HttpPost("{channelId}/apply")]
    public Task<IActionResult> Apply(string channelId, CancellationToken ct) =>
        ExecuteAsync(channelId, async () => Ok(await manager.ApplyAsync(GetUserId(), channelId, ct)));

    /// <summary>Returns actual observed status separately from running intent.</summary>
    [HttpGet("{channelId}/status")]
    public Task<IActionResult> Status(string channelId, CancellationToken ct) =>
        ExecuteAsync(channelId, async () => Ok(await manager.GetStatusAsync(GetUserId(), channelId, ct)));

    /// <summary>Starts a connection from persisted configuration.</summary>
    [HttpPost("{channelId}/start")]
    public Task<IActionResult> Start(string channelId, CancellationToken ct) =>
        ExecuteAsync(channelId, async () => Ok(await manager.StartAsync(GetUserId(), channelId, ct)));

    /// <summary>Stops the connection and drains its admitted work.</summary>
    [HttpPost("{channelId}/stop")]
    public Task<IActionResult> Stop(string channelId, CancellationToken ct) =>
        ExecuteAsync(channelId, async () => Ok(await manager.StopAsync(GetUserId(), channelId, ct)));

    private async Task<IActionResult> ExecuteAsync(string channelId, Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException ex) { return NotFound(new ProblemDetails { Title = "Channel unavailable", Detail = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new ProblemDetails { Title = "Invalid channel settings", Detail = ex.Message }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Channel operation failed for user {UserId} on {ChannelId}.", GetUserId(), channelId);
            return Problem(title: "Channel operation failed", detail: "Check the saved settings, credentials, and connection logs.");
        }
    }
}
