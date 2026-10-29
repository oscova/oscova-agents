using Microsoft.Extensions.Localization;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Notifications;

namespace Oscova.Agents.Host.Services;

/// <summary>Discovers both release sources by default and announces each component identity once.</summary>
public sealed class UpdateDiscoveryService(ApplicationUpdateService harness, AvatarUpdateService avatar,
    UpdateNotificationSettings settings, NotificationHub<NotificationMessage> notifications,
    INotificationInbox inbox, IStringLocalizer<SettingsText> text, TimeProvider clock,
    ILogger<UpdateDiscoveryService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Uses the same independent discovery and announcement path for polling and manual refresh.</summary>
    public async Task CheckAsync(bool refresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await settings.LoadAsync().ConfigureAwait(false);
            try
            {
                var status = await harness.GetStatusAsync(refresh, ct).ConfigureAwait(false);
                if (status is { State: "Available", AvailableVersion: not null } && settings.HarnessIdentity != status.AvailableVersion)
                {
                    await AnnounceAsync(new(
                        text.GetOrDefault(SettingsTextKeys.HarnessUpdateNoticeTitle, "Harness update available"),
                        string.Format(text.GetOrDefault(SettingsTextKeys.HarnessUpdateNoticeBody, "Oscova {0} is available. Open Dashboard Updates to review it."), status.AvailableVersion),
                        NotificationKind.HarnessUpdate), ct).ConfigureAwait(false);
                    settings.HarnessIdentity = status.AvailableVersion;
                    await settings.SaveAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Harness release discovery could not complete."); }

            try
            {
                var status = await avatar.GetStatusAsync(refresh, ct).ConfigureAwait(false);
                if (status.AvailableRelease is { } release)
                {
                    var identity = $"{release.Platform}:{release.SizeBytes}:{release.Sha256.ToUpperInvariant()}";
                    if (settings.AvatarIdentity != identity)
                    {
                        await AnnounceAsync(new(
                            text.GetOrDefault(SettingsTextKeys.AvatarUpdateNoticeTitle, "Avatar Engine available"),
                            string.Format(text.GetOrDefault(SettingsTextKeys.AvatarUpdateNoticeBody, "Avatar Engine {0} is available. Open Dashboard Updates to review installation on this computer."), release.Version),
                            NotificationKind.AvatarUpdate), ct).ConfigureAwait(false);
                        settings.AvatarIdentity = identity;
                        await settings.SaveAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Avatar release discovery could not complete."); }
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await CheckAsync(false, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Update discovery could not complete."); }

                await Task.Delay(TimeSpan.FromHours(6), clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task AnnounceAsync(NotificationMessage message, CancellationToken ct)
    {
        var previous = (await inbox.GetSystemAsync(ct).ConfigureAwait(false)).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        await notifications.NotifyAllAsync(message).ConfigureAwait(false);
        // The hub isolates subscriber failures, so verify storage before suppressing later discovery.
        var saved = await inbox.GetSystemAsync(ct).ConfigureAwait(false);
        if (!saved.Any(entry => entry.Kind == message.Kind && !previous.Contains(entry.Id)))
        {
            throw new IOException("The update notification could not be saved. Discovery will try again.");
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _gate.Dispose();
    }
}
