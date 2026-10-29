using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Notifications;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Host.Server.Hubs;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Host.Server.Services;

public sealed class SignalRNotificationRelay(
    NotificationHub<TaskStatusUpdate> taskStatusNotifications,
    NotificationHub<ChannelStatus> channelStatusNotifications,
    NotificationHub<NotificationMessage> notificationMessages,
    NotificationHub<ChatThreadChanged> chatThreadNotifications,
    IHubContext<SystemHub> systemHubContext,
    IServiceScopeFactory serviceScopeFactory,
    INotificationInbox notificationInbox,
    UserServiceManager userServiceManager,
    IAgentCatalog agentCatalog) : IHostedService, IDisposable
{
    private IDisposable? _taskStatusSubscription;
    private IDisposable? _channelStatusSubscription;
    private IDisposable? _notificationSubscription;
    private IDisposable? _chatThreadSubscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _taskStatusSubscription = taskStatusNotifications.SubscribeAll(PublishTaskStatusAsync);
        _channelStatusSubscription = channelStatusNotifications.SubscribeAll(PublishChannelStatusAsync);
        _notificationSubscription = notificationMessages.SubscribeAll(PublishNotificationAsync);
        _chatThreadSubscription = chatThreadNotifications.SubscribeAll(PublishThreadChangedAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeSubscriptions();
        return Task.CompletedTask;
    }

    private Task PublishTaskStatusAsync(string? userId, TaskStatusUpdate payload)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.CompletedTask;
        }

        return systemHubContext.Clients.Group(HubGroupNames.ForUser(userId))
            .SendAsync(SystemHubEvents.TaskStatusUpdated, payload);
    }

    private Task PublishChannelStatusAsync(string? userId, ChannelStatus payload)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.CompletedTask;
        }

        return systemHubContext.Clients.Group(HubGroupNames.ForUser(userId))
            .SendAsync(SystemHubEvents.ChannelStatusChanged, payload);
    }

    private async Task PublishNotificationAsync(string? userId, NotificationMessage payload)
    {
        var targetUserId = string.IsNullOrWhiteSpace(userId) ? payload.UserId : userId;
        if (string.IsNullOrWhiteSpace(targetUserId))
        {
            await PublishSystemNotificationAsync(payload).ConfigureAwait(false);
            return;
        }

        if (!await notificationInbox.ShouldDeliverToUserAsync(targetUserId, payload.Kind).ConfigureAwait(false))
        {
            return;
        }

        await systemHubContext.Clients.Group(HubGroupNames.ForUser(targetUserId))
            .SendAsync(SystemHubEvents.NotificationReceived, WithLabels(targetUserId, payload)).ConfigureAwait(false);
    }

    // Notification text stores provider and MCP server ids; each recipient sees its own labels.
    private NotificationMessage WithLabels(string userId, NotificationMessage payload)
    {
        if (!userServiceManager.TryGetExistingScope(userId, out var scope)) { return payload; }

        var labels = UserLabelMap.Create(scope, agentCatalog);
        return payload with { Title = labels.ReplaceIds(payload.Title), Body = labels.ReplaceIds(payload.Body) };
    }

    private async Task PublishSystemNotificationAsync(NotificationMessage payload)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admins = await userManager.GetUsersInRoleAsync("Admin").ConfigureAwait(false);

        foreach (var admin in admins)
        {
            if (string.IsNullOrWhiteSpace(admin.Id) || !await notificationInbox.ShouldDeliverToUserAsync(admin.Id, payload.Kind).ConfigureAwait(false))
            {
                continue;
            }

            await systemHubContext.Clients.Group(HubGroupNames.ForUser(admin.Id))
                .SendAsync(SystemHubEvents.NotificationReceived, WithLabels(admin.Id, payload)).ConfigureAwait(false);
        }
    }

    private Task PublishThreadChangedAsync(string? userId, ChatThreadChanged payload)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.CompletedTask;
        }

        // The current thread-change notification is a cache invalidation signal, not a full thread delta.
        return systemHubContext.Clients.Group(HubGroupNames.ForUser(userId))
            .SendAsync(SystemHubEvents.ThreadChanged, new ChatThreadChangedEvent(DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        DisposeSubscriptions();
        GC.SuppressFinalize(this);
    }

    private void DisposeSubscriptions()
    {
        _taskStatusSubscription?.Dispose();
        _taskStatusSubscription = null;

        _channelStatusSubscription?.Dispose();
        _channelStatusSubscription = null;

        _notificationSubscription?.Dispose();
        _notificationSubscription = null;

        _chatThreadSubscription?.Dispose();
        _chatThreadSubscription = null;
    }
}
