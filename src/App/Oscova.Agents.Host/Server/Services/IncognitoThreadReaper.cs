using Oscova.Agents.Conversations;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Services;

/// <summary>
/// Discards idle incognito chat threads and clears attachments left behind by a previous process.
/// </summary>
public sealed class IncognitoThreadReaper(
    UserServiceManager userServiceManager,
    ChatSessionService chatSessionService,
    ThreadRunGuard threadRunGuard,
    ILogger<IncognitoThreadReaper> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Wipes the ephemeral attachment root once, then periodically discards idle incognito threads.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Incognito thread reaper started.");

        // A crash or a kill never runs the discard path, so clear whatever the last process staged.
        try
        {
            ChatAttachmentService.WipeIncognitoRoot();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to wipe the incognito attachment root at startup.");
        }

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ReapIdleThreadsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Incognito thread reap failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogDebug("Incognito thread reaper stopped.");
        }
    }

    private async Task ReapIdleThreadsAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - IdleWindow;
        foreach (var userId in userServiceManager.ActiveUserIds)
        {
            // Never construct a scope from a background tick.
            if (!userServiceManager.TryGetExistingScope(userId, out var scope))
            {
                continue;
            }

            var threads = await scope.ChatThreadRepository.GetIncognitoThreadsAsync(userId, ct).ConfigureAwait(false);
            foreach (var thread in threads)
            {
                // A live run would let the dispatch agent recreate the thread as a persisted one.
                if (thread.UpdatedAt > cutoff || threadRunGuard.GetActiveRunId(userId, thread.Id) is not null)
                {
                    continue;
                }

                await chatSessionService.DeleteThreadAsync(userId, thread.Id, ct).ConfigureAwait(false);
            }
        }
    }
}
