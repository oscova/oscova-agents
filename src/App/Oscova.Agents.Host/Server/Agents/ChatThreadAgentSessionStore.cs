using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Conversations;
using Microsoft.Agents.AI;
using Oscova.Agents.Host.Server.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.ToolGovernance;
using Oscova.Agents.Infrastructure.Hooks;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Agents;

/// <summary>Persists framework sessions in user-owned chat threads under explicit server ownership.</summary>
public sealed class ChatThreadAgentSessionStore(
    UserServiceManager userServiceManager,
    Oscova.Agents.Core.Interfaces.IChatAttachmentService chatAttachmentService,
    IAgentRuntimeHookService runtimeHookService,
    RunContextAccessor runContextAccessor,
    IToolQuotaUsageStore quotaUsageStore,
    ILogger<ChatThreadAgentSessionStore> logger)
{
    private const string RunOutcomeStateKey = "Oscova.Chat.RunOutcome";

    // Final persistence must not rebuild a model client while its configuration is changing.
    private readonly ConditionalWeakTable<AgentSession, ResolvedOwnerAgent> _sessionOwners = new();

    /// <summary>Reads the saved outcome without constructing an agent to render the composer.</summary>
    internal static ChatRunOutcome GetRunOutcome(JsonElement? sessionElement)
    {
        if (sessionElement is not JsonElement { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("stateBag", out var stateBag) ||
            stateBag.ValueKind != JsonValueKind.Object)
        {
            return ChatRunOutcome.None;
        }

        try
        {
            return ReadRunOutcome(AgentSessionStateBag.Deserialize(stateBag));
        }
        catch (JsonException)
        {
            return ChatRunOutcome.None;
        }
    }

    /// <summary>Reads the outcome from the framework-restored session.</summary>
    internal static ChatRunOutcome GetRunOutcome(AgentSession session) => ReadRunOutcome(session.StateBag);

    /// <summary>Stores the run result inside the existing encrypted session.</summary>
    internal static void SetRunOutcome(AgentSession session, ChatRunOutcome outcome)
    {
        if (outcome == ChatRunOutcome.None)
        {
            session.StateBag.TryRemoveValue(RunOutcomeStateKey);
        }
        else
        {
            session.StateBag.SetValue(RunOutcomeStateKey, outcome.ToString());
        }
    }

    private static ChatRunOutcome ReadRunOutcome(AgentSessionStateBag stateBag)
    {
        stateBag.TryGetValue<string>(RunOutcomeStateKey, out var outcome);
        return outcome switch
        {
            nameof(ChatRunOutcome.Completed) => ChatRunOutcome.Completed,
            nameof(ChatRunOutcome.Stopped) => ChatRunOutcome.Stopped,
            nameof(ChatRunOutcome.Failed) => ChatRunOutcome.Failed,
            _ => ChatRunOutcome.None
        };
    }

    /// <summary>Loads or creates the session for one user-owned chat thread.</summary>
    public async ValueTask<AgentSession> LoadForUserAsync(
            string userId,
            string threadId,
            string? modelProviderId,
            CancellationToken cancellationToken = default)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, cancellationToken).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, cancellationToken).ConfigureAwait(false);

        // Resolve the agent that originally produced the session bytes (recorded as
        // thread.AgentId) instead of trusting the dispatch agent's main-agent fallback.
        // For ChatClientAgent the format is identical, but a future workflow-backed agent
        // would have a divergent shape and silently corrupt without this.
        var ownerAgent = await ResolveOwnerAgentAsync(
            scope,
            userId,
            thread?.AgentId,
            modelProviderId,
            cancellationToken).ConfigureAwait(false);

        if (thread?.BranchOrigin is not null &&
            (thread.SessionElement is null || ownerAgent.AgentId != thread.AgentId))
        {
            throw new InvalidOperationException("The branched chat's original agent or saved context is unavailable.");
        }

        if (thread?.SessionElement is null)
        {
            var session = await ownerAgent.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            await DispatchSessionStartAsync(userId, threadId, ownerAgent.AgentId, cancellationToken).ConfigureAwait(false);
            _sessionOwners.Add(session, ownerAgent);
            return session;
        }

        try
        {
            var session = await ownerAgent.Agent.DeserializeSessionAsync(thread.SessionElement.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
            _sessionOwners.Add(session, ownerAgent);
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (thread.BranchOrigin is not null)
            {
                logger.LogWarning(ex, "Failed to restore branched chat context for thread {ThreadId}.", threadId);
                throw new InvalidOperationException("The branched chat's saved context could not be restored.", ex);
            }

            logger.LogWarning(ex, "Failed to restore saved chat context for thread {ThreadId}.", threadId);
            throw new InvalidOperationException(
                "The saved chat context could not be restored. Use a compatible application or restore a compatible backup.", ex);
        }
    }

    /// <summary>Retains or restores the whole turn so regeneration rewinds every participant together.</summary>
    internal async Task<AgentSession> PrepareConsultationTurnAsync(
        string userId, string threadId, string? modelProviderId, AgentSession session,
        bool regenerate, string messageId, CancellationToken ct)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        var owner = await ResolveOwnerAgentAsync(scope, userId, thread?.AgentId, modelProviderId, ct).ConfigureAwait(false);
        var participants = ChatParticipantSessions.Load(session);
        if (regenerate)
        {
            if (participants.TurnStartMessageId != messageId) { throw new InvalidOperationException("The original user turn checkpoint is unavailable."); }

            var before = participants.TurnStartSession
                ?? throw new InvalidOperationException("The consultation's original turn context is unavailable.");
            session = await owner.Agent.DeserializeSessionAsync(before, cancellationToken: ct).ConfigureAwait(false);
            participants = ChatParticipantSessions.Load(session);
            participants.TurnStartSession = before;
        }
        else
        {
            // Stage on a copy so rejected input can retain the preceding turn, without nested snapshots.
            var previous = participants.TurnStartSession;
            participants.TurnStartSession = null;
            participants.Save(session);
            JsonElement before;
            try
            {
                before = await owner.Agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false);
            }
            finally
            {
                participants.TurnStartSession = previous;
                participants.Save(session);
            }

            session = await owner.Agent.DeserializeSessionAsync(before, cancellationToken: ct).ConfigureAwait(false);
            participants = ChatParticipantSessions.Load(session);
            participants.TurnStartSession = before;
        }

        participants.TurnStartMessageId = messageId;
        participants.Save(session);
        _sessionOwners.Add(session, owner);
        return session;
    }

    /// <summary>Moves regeneration to the applied steer while retaining the completed prefix and every participant.</summary>
    internal async Task CaptureSteeringTurnAsync(
        string userId, string threadId, string? modelProviderId, AgentSession ownerSession,
        ChatClientAgent recipient, AgentSession recipientSession, IReadOnlyList<ChatMessage> history,
        string messageId, CancellationToken ct)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        var owner = await ResolveOwnerAgentAsync(scope, userId, thread!.AgentId, modelProviderId, ct).ConfigureAwait(false);
        var copy = await owner.Agent.DeserializeSessionAsync(
            await owner.Agent.SerializeSessionAsync(ownerSession, cancellationToken: ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var copiedParticipants = ChatParticipantSessions.Load(copy);
        copiedParticipants.TurnStartSession = null;
        var recipientCopy = recipient.Id == owner.AgentId ? copy : await recipient.DeserializeSessionAsync(
            await recipient.SerializeSessionAsync(recipientSession, cancellationToken: ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        recipientCopy.SetInMemoryChatHistory(history.ToList());
        ChatSteeringTarget.RemovePendingMessages(recipientCopy);
        if (recipient.Id != owner.AgentId)
        {
            copiedParticipants.Join(recipient.Id).Session = await recipient.SerializeSessionAsync(recipientCopy, cancellationToken: ct).ConfigureAwait(false);
        }

        copiedParticipants.Save(copy);
        var participants = ChatParticipantSessions.Load(ownerSession);
        participants.TurnStartSession = await owner.Agent.SerializeSessionAsync(copy, cancellationToken: ct).ConfigureAwait(false);
        participants.TurnStartMessageId = messageId;
        participants.Save(ownerSession);
    }

    /// <summary>Saves the session for one user-owned chat thread.</summary>
    public async ValueTask SaveForUserAsync(
        string userId,
        string threadId,
        AgentSession session,
        string? modelProviderId,
        CancellationToken cancellationToken = default)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, cancellationToken).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, cancellationToken)
            .ConfigureAwait(false);
        if (thread is null)
        {
            return;
        }

        if (!_sessionOwners.TryGetValue(session, out var ownerAgent))
        {
            ownerAgent = await ResolveOwnerAgentAsync(
                scope,
                userId,
                thread.AgentId,
                modelProviderId,
                cancellationToken).ConfigureAwait(false);
        }

        JsonElement serialized = await ownerAgent.Agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        await scope.ChatThreadRepository.SaveSessionElementAsync(
            userId,
            threadId,
            serialized,
            ownerAgent.AgentId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retains the final persisted state for an eligible completed web reply.</summary>
    public async Task CaptureCheckpointForUserAsync(string userId, string threadId, string messageId,
        CancellationToken ct = default)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        if (thread?.SessionElement is not { } session ||
            GetRunOutcome(session) != ChatRunOutcome.Completed ||
            thread.DisplayMessages.LastOrDefault() is not { From: MessageOwner.Bot } message ||
            message.MessageId != messageId ||
            !message.Segments.Any(segment => segment.Type == ChatSegmentType.Text &&
                !string.IsNullOrWhiteSpace(segment.Text)))
        {
            return;
        }

        var checkpoint = new ChatTurnCheckpoint(thread,
            chatAttachmentService.GetToolOutputFileNames(userId, thread.Id, thread.IsIncognito));
        var checkpoints = thread.SessionCheckpoints!.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        checkpoints[messageId] = JsonSerializer.SerializeToElement(checkpoint);
        thread.SessionCheckpoints = checkpoints;
        await scope.ChatThreadRepository.SaveThreadAsync(userId, thread, setCurrent: false, ct: ct)
            .ConfigureAwait(false);
    }

    /// <summary>Discards a reply snapshot when Stop interrupts its final persistence.</summary>
    public async Task DiscardCheckpointForUserAsync(string userId, string threadId, string messageId,
        CancellationToken ct = default)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        if (thread?.SessionCheckpoints is not { } retained || !retained.ContainsKey(messageId)) { return; }

        var checkpoints = retained.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        checkpoints.Remove(messageId);
        thread.SessionCheckpoints = checkpoints;
        await scope.ChatThreadRepository.SaveThreadAsync(userId, thread, setCurrent: false, ct: ct)
            .ConfigureAwait(false);
    }

    private async Task<ResolvedOwnerAgent> ResolveOwnerAgentAsync(
        UserServiceScope scope,
        string userId,
        string? agentId,
        string? modelProviderId,
        CancellationToken cancellationToken)
    {
        var definition = scope.AppService.ResolveAgentDefinition(agentId);
        var agent = await scope.AppService.GetOrCreateAgent(
            userId,
            definition,
            modelProviderId,
            configureAgentBuilder: (agentDefinition, builder) => builder.UseAgentToolHooks(
                runtimeHookService,
                runContextAccessor,
                agentDefinition.Id,
                scope.ToolGovernanceSettings,
                quotaUsageStore,
                scope.CrewService.Tools.GetNetworkScope,
                logger)).ConfigureAwait(false);
        return new ResolvedOwnerAgent(agent, definition.Id);
    }

    private Task DispatchSessionStartAsync(
        string userId,
        string chatThreadId,
        string agentId,
        CancellationToken cancellationToken)
    {
        return runtimeHookService.DispatchSessionStartAsync(new SessionHookContext
        {
            UserId = userId,
            AgentId = agentId,
            ChatThreadId = chatThreadId,
            ExecutionKind = HookExecutionKinds.WebChat
        }, cancellationToken);
    }

    private sealed record ResolvedOwnerAgent(ChatClientAgent Agent, string AgentId);
}
