using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>
/// Disposes idle shell executors so abandoned conversation containers do not linger indefinitely.
/// </summary>
public sealed class ShellExecutorReaper(
    ShellExecutorFactory factory,
    IShellBackgroundJobProvider backgroundJobs,
    ILogger<ShellExecutorReaper> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Periodically evicts shell executors whose conversations have been idle.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Shell executor idle reaper started.");
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await factory.ReapIdleAsync(IdleWindow, backgroundJobs.HasLiveJobs).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Shell executor idle reap failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogDebug("Shell executor idle reaper stopped.");
        }
    }
}
