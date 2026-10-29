using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Definitions;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Providers;

namespace Oscova.Agents.Host.Components.Chat;

public partial class ChatPanel
{
    private IReadOnlyList<AgentConversationView> _agentConversations = [];
    private string? _selectedAgentConversationKey;
    private IReadOnlyList<ChatDisplayMessage> _selectedAgentMessages = [];
    private Task? _agentConversationPolling;

    // Same-agent workers share one definition and may share a name, so the worker handle identifies the tab.
    private static string GetAgentConversationKey(AgentConversationView conversation) =>
        string.IsNullOrEmpty(conversation.Handle)
            ? $"{Uri.EscapeDataString(conversation.AgentId)}|{Uri.EscapeDataString(conversation.Name ?? string.Empty)}"
            : conversation.Handle;

    private string GetAgentConversationStatus(AgentConversationView conversation) => conversation.Status switch
    {
        DelegatedWorkerStatus.Running => Loc.GetOrDefault(ChatTextKeys.ToolCardStatusRunning, "Running"),
        DelegatedWorkerStatus.AwaitingApproval => Loc.GetOrDefault(ChatTextKeys.AgentConversationStatusAwaitingApproval, "Awaiting approval"),
        DelegatedWorkerStatus.Failed => Loc.GetOrDefault(ChatTextKeys.ToolCardStatusFailed, "Failed"),
        DelegatedWorkerStatus.Interrupted => Loc.GetOrDefault(ChatTextKeys.AgentConversationStatusInterrupted, "Interrupted"),
        _ => Loc.GetOrDefault(ChatTextKeys.ToolCardStatusCompleted, "Completed")
    };

    private void SelectAgentConversation(string? key)
    {
        _selectedAgentConversationKey = key;
        RefreshSelectedAgentMessages();
        RequestAutoScroll();
    }

    /// <summary>Projects the redacted child transcript through the shared tool-call parser.</summary>
    private void RefreshSelectedAgentMessages()
    {
        var conversation = _agentConversations.FirstOrDefault(conversation =>
            string.Equals(GetAgentConversationKey(conversation), _selectedAgentConversationKey, StringComparison.Ordinal));
        var messages = new List<ChatDisplayMessage>();
        ChatSegmentParser? parser = null;
        foreach (var message in conversation?.Messages ?? [])
        {
            if (message.Role == ChatRole.User)
            {
                if (string.IsNullOrWhiteSpace(message.Text)) { continue; }

                parser?.Complete();
                parser = null;
                messages.Add(new ChatDisplayMessage
                {
                    From = MessageOwner.User,
                    Text = message.Text ?? string.Empty,
                    CreatedAt = message.CreatedAt ?? default
                });
            }
            else if (message.Role == ChatRole.Assistant || message.Role == ChatRole.Tool)
            {
                if (parser is null)
                {
                    parser = new ChatSegmentParser();
                    messages.Add(new ChatDisplayMessage { Segments = parser.Segments });
                }

                // Retained tool results match calls by ID, including textless assistant messages.
                parser.Process(new AgentResponseUpdate(message.Role,
                    [.. message.Contents.Where(content => content is TextContent or FunctionCallContent or FunctionResultContent)])
                {
                    CreatedAt = message.CreatedAt
                });
                parser.Complete();
            }
        }

        // History has no live timing observer. Keep recorded timestamps without inventing durations.
        foreach (var message in messages)
        {
            message.Segments = [.. message.Segments.Select(segment => new ChatSegment(
                segment.Type, segment.State, segment.Updates.FirstOrDefault()?.CreatedAt ?? default,
                processedAt: null, segment.Text, segment.FunctionCall, segment.FunctionResult, segment.ExecutionState))];
        }

        _selectedAgentMessages = messages;
    }

    // Same-agent workers of the main agent carry the main agent's definition ID.
    private AgentDefinition? FindConversationAgent(string agentId) =>
        string.Equals(MainAgent.Id, agentId, StringComparison.Ordinal)
            ? MainAgent
            : CustomAgents.FirstOrDefault(agent => string.Equals(agent.Id, agentId, StringComparison.Ordinal));

    /// <summary>Builds the tab label from the current definition name and the caller-supplied conversation name.</summary>
    private string GetAgentConversationLabel(AgentConversationView conversation)
    {
        var name = FindConversationAgent(conversation.AgentId)?.Name
            ?? Loc.GetOrDefault(ChatTextKeys.AssistantFallbackName, "Assistant");
        return string.IsNullOrWhiteSpace(conversation.Name) ? name : $"{name}: {conversation.Name}";
    }

    // The page passes a placeholder agent with a random id until its agent configuration loads.
    private string? GetAgentPictureUrl(string agentId) =>
        string.IsNullOrWhiteSpace(agentId) || !MainAgent.IsMain
            ? null
            : $"/{ApiRoutes.ProfilePictures.AgentById(agentId)}?v={ProfilePictureRefreshState.Version}";

    // ponytail: polls every 2 seconds during a live run, a server push of worker changes can replace it if needed.
    private void EnsureAgentConversationPolling()
    {
        if (!HasLiveRun || _agentConversationPolling is { IsCompleted: false }) { return; }

        _agentConversationPolling = PollAgentConversationsAsync();
    }

    // Workers report progress outside the parent's projection, so their tabs refresh on a timer while the run is live.
    private async Task PollAgentConversationsAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync() && !_isDisposed && HasLiveRun)
        {
            await RefreshAgentConversationsAsync();
            if (_isDisposed) { return; }

            StateHasChanged();
        }
    }

    private async Task RefreshAgentConversationsAsync()
    {
        var thread = ChatThread;

        if (string.IsNullOrWhiteSpace(thread.Id))
        {
            _agentConversations = [];
            _selectedAgentConversationKey = null;
            _selectedAgentMessages = [];
            return;
        }

        try
        {
            var loaded = await ChatApiClient.GetAgentConversationsAsync(
                thread.Id,
                SelectedProvider?.Id.ToString());

            // A delayed response must not replace another conversation's tabs.
            if (!ReferenceEquals(ChatThread, thread))
            {
                return;
            }

            // Only conversations whose agent is still visible get a view.
            _agentConversations = [.. loaded.Where(conversation => FindConversationAgent(conversation.AgentId) is not null)];
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to refresh agent conversations for thread {ThreadId}.", thread.Id);
            if (!ReferenceEquals(ChatThread, thread)) { return; }

            _agentConversations = [];
        }

        if (_selectedAgentConversationKey is not null &&
            !_agentConversations.Any(conversation =>
                string.Equals(GetAgentConversationKey(conversation), _selectedAgentConversationKey, StringComparison.Ordinal)))
        {
            _selectedAgentConversationKey = null;
        }

        RefreshSelectedAgentMessages();
    }
}
