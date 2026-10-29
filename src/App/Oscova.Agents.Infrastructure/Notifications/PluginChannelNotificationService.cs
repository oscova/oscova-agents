using Oscova.Agents.Harness;
using System.Text.Json;
using Oscova.Agents.Core.Channels;
using Oscova.Agents.Tasks;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Chat;

namespace Oscova.Agents.Infrastructure.Notifications;

/// <summary>Delivers notifications and binds channel replies to the originating conversation.</summary>
public sealed class PluginChannelNotificationService(string channelId, IChannelManager manager,
    WebNotificationService notifications, ChannelConversationBindings bindings, AssistantUiSettings uiSettings) : IChannelNotificationService
{
    /// <inheritdoc />
    public string Channel => channelId;
    /// <inheritdoc />
    public async Task HandoffAsync(string userId, string agentId, JsonElement sessionElement, string message, string? title,
        CancellationToken ct = default)
    {
        await RequireRunningAsync(userId, ct).ConfigureAwait(false);
        var thread = await notifications.CreateHandoffAsync(userId, agentId, sessionElement, message, title, ct).ConfigureAwait(false);
        await SendAndBindAsync(userId, thread.Id, message, ct).ConfigureAwait(false);
    }

    /// <summary>Notifies from an owned chat thread without copying its live session.</summary>
    public async Task NotifyThreadAsync(string userId, string threadId, string message, CancellationToken ct = default)
    {
        await RequireRunningAsync(userId, ct).ConfigureAwait(false);
        var thread = await notifications.GetOwnedThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        await SendAndBindAsync(userId, thread.Id, message, ct).ConfigureAwait(false);
    }

    private async Task SendAndBindAsync(string userId, string threadId, string message, CancellationToken ct)
    {
        await manager.SendNotificationAsync(userId, channelId, message, ct).ConfigureAwait(false);
        bindings.Bind(userId, channelId, threadId, uiSettings.ChatResumeWindowMinutes);
    }

    private async Task RequireRunningAsync(string userId, CancellationToken ct)
    {
        var configuration = await manager.GetAsync(userId, channelId, ct).ConfigureAwait(false);
        if (!configuration.Status.IsRunning)
        {
            throw new ToolFailureException("The channel is not running. Start it in channel settings before sending notifications.");
        }
    }
}
