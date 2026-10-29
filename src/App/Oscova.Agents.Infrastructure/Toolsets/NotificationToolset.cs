using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Infrastructure.Notifications;
using Oscova.Agents.Harness;
using Oscova.Agents.Tasks;
using System.ComponentModel;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>
/// Notifies the user from a chat thread or hands off an agent task's session.
/// </summary>
public sealed class NotificationToolset(
    RunContextAccessor runContextAccessor,
    IEnumerable<IChannelNotificationService> channelServices) : IAgentToolset
{
    // Intentionally omit ToolNetworkScope: user-configured channels are user-owned, not product egress.
    [DisplayName("notify")]
    [AgentTool]
    [Description("""
        Send the user a message from this conversation or agent task.
        Chat runs require a running messaging channel from notification_channels in Runtime Context.
        Chat notifications keep this thread. Task notifications create a handoff thread.
        title names a task's handoff thread.
        Channel replies use that thread and its agent for the configured resume window counted from notify.
        Each notification replaces the channel binding. A zero window disables binding.
        /new or a host restart ends the binding.
        """)]
    public async Task<string> NotifyUserAsync(
        [Description("Nonblank message for the user.")] string message,
        [Description("One of notification_channels in Runtime Context. Default: web.")] string channel = NotificationChannels.Web,
        [Description("Optional handoff thread title for task notifications. Maximum 120 characters, longer titles are cut.")] string? title = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ToolFailureException("A notification message is required.");
        }

        var run = runContextAccessor.Current
            ?? throw new ToolFailureException("Notifications require an active chat or agent task.");
        var task = run.Task;

        var service = channelServices.FirstOrDefault(
            candidate => candidate.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            var available = string.Join(", ", channelServices.Select(candidate => candidate.Channel));
            throw new ToolFailureException($"channel '{channel}' is not available. Use one of: {available}.");
        }

        if (task is null)
        {
            if (service is not PluginChannelNotificationService chatService)
            {
                throw new ToolFailureException($"channel '{channel}' does not support notifications from a chat thread.");
            }

            await chatService.NotifyThreadAsync(run.UserId, run.ExecutionId, message, ct).ConfigureAwait(false);
            return $"User notified on {service.Channel}. Replies continue this conversation within the channel resume window.";
        }

        // The reflected run token must reach session capture and channel delivery.
        JsonElement? sessionElement = null;
        if (task.SerializeLiveSessionAsync is not null)
        {
            sessionElement = await task.SerializeLiveSessionAsync(ct).ConfigureAwait(false);
        }

        if (!sessionElement.HasValue)
        {
            sessionElement = task.SessionElement;
        }

        if (!sessionElement.HasValue)
        {
            throw new ToolFailureException("The current session cannot be handed off because no session data is available.");
        }

        await service.HandoffAsync(task.UserId, task.AgentId, sessionElement.Value, message, title, ct).ConfigureAwait(false);
        return $"User notified on {service.Channel}. The conversation was handed off.";
    }
}
