using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using System.Text.Json;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Notifications;
using Oscova.Agents.Tasks;
using Oscova.Agents.Harness;

namespace Oscova.Agents.Infrastructure.Notifications;

public class WebNotificationService(
    IChatThreadRepository chatThreadRepository,
    IAgentDefinitionRepository agentDefinitionStore,
    NotificationHub<ChatThreadChanged> chatThreadNotifications,
    NotificationHub<NotificationMessage> notificationMessages) : IChannelNotificationService
{
    public string Channel => NotificationChannels.Web;

    public Task HandoffAsync(string userId, string agentId, JsonElement sessionElement, string message, string? title,
        CancellationToken ct = default) =>
        CreateHandoffAsync(userId, agentId, sessionElement, message, title, ct);

    // Channel bindings select their own destination without changing web selection.
    internal async Task<ChatThread> CreateHandoffAsync(string userId, string agentId, JsonElement sessionElement, string message,
        string? title, CancellationToken ct)
    {
        var thread = await CreateNotificationThreadAsync(userId, agentId, message, title, sessionElement, ct).ConfigureAwait(false);
        await chatThreadRepository.CreateThreadAsync(userId, thread, setCurrent: false, ct).ConfigureAwait(false);
        await chatThreadNotifications.NotifyAsync(userId, new ChatThreadChanged()).ConfigureAwait(false);
        await PublishBellNotificationAsync(userId, agentId, thread).ConfigureAwait(false);
        return thread;
    }

    internal async Task<ChatThread> GetOwnedThreadAsync(string userId, string threadId, CancellationToken ct)
        => await chatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false)
            ?? throw new ToolFailureException("The conversation is unavailable. Start a chat before sending a notification.");

    private async Task<ChatThread> CreateNotificationThreadAsync(
        string userId,
        string agentId,
        string message,
        string? title,
        JsonElement sessionElement,
        CancellationToken ct)
    {
        var agentDef = await agentDefinitionStore.GetAgentAsync(userId, agentId, ct).ConfigureAwait(false);
        var agentName = agentDef?.Name ?? "Assistant";
        title = title?.Trim();

        // An agent-chosen title follows the thread rename limit without splitting a surrogate pair.
        if (title?.Length > RenameThreadRequest.MaxTitleLength)
        {
            var length = char.IsHighSurrogate(title[RenameThreadRequest.MaxTitleLength - 1]) ? RenameThreadRequest.MaxTitleLength - 1 : RenameThreadRequest.MaxTitleLength;
            title = title[..length].TrimEnd();
        }

        return new ChatThread
        {
            Title = string.IsNullOrEmpty(title) ? $"Handoff from {agentName}" : title,
            AgentId = agentId,
            AgentName = agentName,
            IsUnread = true,
            SessionElement = sessionElement,
            UpdatedAt = DateTimeOffset.UtcNow,
            DisplayMessages =
            [
                new ChatDisplayMessage
                {
                    Channel = HookExecutionKinds.WebChat,
                    From = MessageOwner.Bot,
                    Text = message,
                    Segments =
                    [
                        new ChatSegment(
                            ChatSegmentType.Text,
                            ChatSegmentState.Completed,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            message,
                            null)
                    ]
                }
            ]
        };
    }

    private Task PublishBellNotificationAsync(string userId, string agentId, ChatThread thread)
    {
        // Private content belongs in the encrypted conversation, never in the plaintext bell inbox.
        return notificationMessages.NotifyAsync(userId, new NotificationMessage(
            "Notification",
            "Open the conversation to read the message.",
            NotificationKind.ChatMessage,
            UserId: userId,
            AgentId: agentId,
            ThreadId: thread.Id));
    }
}
