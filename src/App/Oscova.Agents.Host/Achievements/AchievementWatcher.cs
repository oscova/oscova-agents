using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Server.Services;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Notifications;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Host.Achievements;

/// <summary>
/// Publishes newly unlocked achievements from the existing dashboard snapshot cache.
/// </summary>
public sealed class AchievementWatcher(
    IServiceProvider serviceProvider,
    DashboardSnapshotCache cache,
    IAppSettingsRepository settingsRepository,
    NotificationHub<NotificationMessage> notifications,
    IStringLocalizer<AchievementText> localizer,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private readonly ILogger<AchievementWatcher> _logger = loggerFactory.CreateLogger<AchievementWatcher>();
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.Ordinal);
    private HashSet<string>? _startupUserIds;

    /// <summary>
    /// Checks cached progress once per metrics interval until the host stops.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("Achievement watcher started.");

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Achievement watcher tick failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _logger.LogDebug("Achievement watcher stopped.");
        }
    }

    /// <summary>
    /// Processes the current identity roster against cached dashboard snapshots.
    /// </summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var scope = serviceProvider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var userIds = await userManager.Users
                .Where(user => user.Id != WellKnownUsers.SystemId)
                .Select(user => user.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            // Capture the process-start roster before snapshots so late cache entries are still baselined.
            _startupUserIds ??= [.. userIds];

            foreach (var userId in userIds)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = cache.TryGet(userId);
                if (snapshot is null)
                {
                    continue;
                }

                try
                {
                    await ProcessUserAsync(userId, snapshot).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish achievement notifications for user {UserId}.", userId);
                }
            }
        }
    }

    private async Task ProcessUserAsync(string userId, DashboardSnapshot snapshot)
    {
        var unlocked = AchievementEvaluator.Evaluate(snapshot).Achievements
            .Where(achievement => achievement.IsUnlocked)
            .Select(achievement => achievement.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (!_seen.TryGetValue(userId, out var previous))
        {
            // Existing accounts start silently; accounts created after the roster announce their first set.
            previous = _startupUserIds!.Contains(userId)
                ? unlocked
                : new HashSet<string>(StringComparer.Ordinal);
            _seen[userId] = previous;
        }

        foreach (var key in unlocked)
        {
            if (previous.Contains(key))
            {
                continue;
            }

            // Delivery is best effort; only failures before the hub call are observable here.
            await PublishAsync(userId, key).ConfigureAwait(false);
            previous.Add(key);
        }

        previous.IntersectWith(unlocked);
    }

    private async Task PublishAsync(string userId, string key)
    {
        var settings = new AssistantUiSettings(settingsRepository, userId, loggerFactory);
        await settings.LoadAsync().ConfigureAwait(false);
        if (!settings.IsGamificationEnabled)
        {
            return;
        }

        string title;
        string body;
        using (new CultureScope(settings.CultureName))
        {
            // The inbox deduplicates by kind and title, so every achievement uses its own name.
            title = localizer.GetOrDefault($"{key}.Name", key);
            body = localizer.GetOrDefault(AchievementTextKeys.UnlockedBody, "Achievement unlocked.");
        }

        await notifications.NotifyAsync(
            userId,
            new NotificationMessage(title, body, NotificationKind.Generic, userId)).ConfigureAwait(false);
    }
}
