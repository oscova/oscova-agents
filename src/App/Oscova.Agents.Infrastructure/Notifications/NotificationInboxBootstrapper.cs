using Microsoft.Extensions.Hosting;
using Oscova.Agents.Core.Notifications;

namespace Oscova.Agents.Infrastructure.Notifications;

/// <summary>
/// Forces the notification inbox singleton to subscribe to the notification hub during host startup.
/// </summary>
public sealed class NotificationInboxBootstrapper(INotificationInbox inbox) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = inbox;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
