using System.Net.WebSockets;
using Microsoft.Extensions.Caching.Memory;
using Oscova.Agents.Host.Server.Security;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Browsing;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Transports authorized browser input and images between the user's chat and its plugin runtime.</summary>
[ApiController, Authorize, Route("api/browser/{threadId}")]
public sealed class BrowserController(UserServiceManager users, IServiceScopeFactory scopes,
    ILogger<BrowserController> logger, IMemoryCache cache) : UserScopeControllerBase(users)
{
    /// <summary>Executes manual browser input after resolving the authenticated chat owner.</summary>
    [HttpPost("command"), ValidateAntiForgeryToken, RequestSizeLimit(46_000_000)]
    public async Task<IActionResult> Command(string threadId, BrowserCommand command, CancellationToken ct)
    {
        if (!SameOrigin()) { return Forbid(); }

        try
        {
            var browser = await GetBrowserAsync(ct);
            return Ok(await browser.ExecuteAsync(threadId, command, ct));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception exception) when (exception is ArgumentException or ToolFailureException)
        { return Problem(title: "Browser action failed", detail: exception.Message, statusCode: StatusCodes.Status400BadRequest); }
    }

    /// <summary>Streams page images while continuing to verify account, vault and chat access.</summary>
    [HttpGet("{pageId}/frames")]
    public async Task Frames(string threadId, string pageId, CancellationToken ct)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest || !SameOrigin()) { Response.StatusCode = 400; return; }

        var browser = await GetBrowserAsync(ct);
        await browser.ResolveSessionAsync(threadId, ct);
        if (!browser.Capability.GetPages(threadId).Any(page => page.Id == pageId)) { Response.StatusCode = 404; return; }

        var authentication = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = WatchCloseAsync(socket, ended);
        var authorization = WatchAccessAsync(browser, threadId, authentication.Properties?.ExpiresUtc, ended);
        try
        {
            await foreach (var frame in browser.Capability.StreamAsync(threadId, pageId, ended.Token))
            {
                var metadata = JsonSerializer.SerializeToUtf8Bytes(new { width = frame.Width, height = frame.Height, pointerX = frame.PointerX, pointerY = frame.PointerY, pointerRevision = frame.PointerRevision });
                await socket.SendAsync(metadata, WebSocketMessageType.Text, true, ended.Token);
                await socket.SendAsync(frame.Jpeg, WebSocketMessageType.Binary, true, ended.Token);
            }
        }
        catch (OperationCanceledException) when (ended.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally
        {
            await ended.CancelAsync();
            await Task.WhenAll(receive, authorization);
        }
    }

    /// <summary>Downloads one completed website file belonging to this chat and page.</summary>
    [HttpGet("{pageId}/downloads/{downloadId}")]
    public async Task<IActionResult> Download(string threadId, string pageId, string downloadId, CancellationToken ct)
    {
        var browser = await GetBrowserAsync(ct);
        var session = await browser.ResolveSessionAsync(threadId, ct);
        var result = await browser.Capability.ExecuteAsync(session, new("downloads", pageId), ct);
        var metadata = result.Downloads?.FirstOrDefault(item => item.Id == downloadId && item.State == "completed");
        if (metadata is null) { return NotFound(); }

        ApplyPrivateFileHeaders();
        return File(await browser.Capability.OpenDownloadAsync(threadId, pageId, downloadId, ct), "application/octet-stream", metadata.Name);
    }

    private async Task<BrowserWorkspaceService> GetBrowserAsync(CancellationToken ct)
    {
        await BrowserViewerAccess.RequireAsync(User, cache, scopes);
        return (await GetUserScopeAsync(ct)).BrowserWorkspace ?? throw new ToolFailureException("Browser capability is unavailable.");
    }

    private bool SameOrigin() => Request.Headers.Origin.Count == 0 ||
        Request.Headers.Origin == $"{Request.Scheme}://{Request.Host}";

    private async Task WatchAccessAsync(BrowserWorkspaceService browser, string threadId, DateTimeOffset? expires, CancellationTokenSource ended)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ended.Token))
            {
                if (expires <= DateTimeOffset.UtcNow) { break; }

                await BrowserViewerAccess.RequireAsync(User, cache, scopes);
                await browser.ResolveSessionAsync(threadId, ended.Token);
            }
        }
        catch (OperationCanceledException) when (ended.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { }
        catch (Exception exception) { logger.LogWarning(exception, "Browser access check failed for chat {ThreadId}.", threadId); }
        finally { await ended.CancelAsync(); }
    }

    private static async Task WatchCloseAsync(WebSocket socket, CancellationTokenSource ended)
    {
        try { await socket.ReceiveAsync(new byte[1], ended.Token); }
        catch (OperationCanceledException) when (ended.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally { await ended.CancelAsync(); }
    }
}
