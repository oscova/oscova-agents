using System.Text.Json;
using Oscova.Agents.Host.Server.Nodes;
using Oscova.Agents.Conversations;
using Microsoft.Agents.AI;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Metrics;
using Oscova.Agents.Notifications;
using Oscova.Agents.Providers;
using Oscova.Agents.Host.Server.Agents;
using Oscova.Agents.Host.Server.Chat;
using Oscova.Agents.Search;
using Oscova.Agents.Toolsets;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable MAAI001

namespace Oscova.Agents.Host.Server.Services;

/// <summary>Describes whether manual compaction found and changed a thread session.</summary>
internal enum CompactThreadResult { NotFound, NoChange, Compacted }

public sealed class ChatSessionService(
    UserServiceManager userServiceManager,
    IAgentCatalog agentCatalog,
    Oscova.Agents.Secrets.IContentProtector protector,
    IServiceScopeFactory serviceScopeFactory,
    Oscova.Agents.Infrastructure.Secrets.PendingDuressSelections pendingDuress,
    NotificationHub<ChatThreadChanged> chatThreadNotifications,
    IChatAttachmentService chatAttachmentService,
    ChatThreadAgentSessionStore sessionStore,
    ThreadRunGuard threadRunGuard,
    ChatRunProjectionStore projectionStore,
    RunContextAccessor runContextAccessor,
    IMetricsEventStore metricsEventStore,
    IEgressAuditStore egressAuditStore,
    IShellExecutorProvider shellExecutorProvider,
    IShellBackgroundJobProvider shellBackgroundJobProvider,
    NodeBroker nodeBroker,
    ChatSteeringRegistry steeringRegistry,
    ILogger<ChatSessionService> logger,
    ModelBudgetService budgets,
    IUserPermissionService permissions,
    Oscova.Agents.Infrastructure.Auth.NodeCredentialStore nodeCredentials)
{
    public async Task<IReadOnlyList<ChatThread>> GetThreadsAsync(string userId, CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var repository = userScope.ChatThreadRepository;
        var threads = await repository.GetThreadsAsync(userId, ct: ct).ConfigureAwait(false);
        var history = threads.ToList();
        // Untitled rows may be pre-run drafts or real attachment-only conversations.
        foreach (var summary in threads.Where(thread => string.IsNullOrWhiteSpace(thread.Title)))
        {
            var thread = await repository.GetThreadAsync(userId, summary.Id, ct: ct).ConfigureAwait(false);
            if (thread is null || thread.DisplayMessages.Count == 0)
            {
                history.Remove(summary);
            }
        }

        return history;
    }

    /// <summary>Searches the user's persisted and incognito conversation text.</summary>
    public async Task<IReadOnlyList<string>> SearchThreadIdsAsync(
        string userId,
        string query,
        CancellationToken ct = default)
    {
        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length < 2)
        {
            return [];
        }

        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var repository = userScope.ChatThreadRepository;
        var summaries = await repository.GetThreadsAsync(userId, ct).ConfigureAwait(false);
        var persisted = await ChatHistorySearch.ReadDocumentsAsync(
            repository, userId, summaries, excludeIncognito: false, ct).ConfigureAwait(false);
        var documents = new List<LexicalSearchDocument>(persisted);
        var incognito = await repository.GetIncognitoThreadsAsync(userId, ct).ConfigureAwait(false);
        foreach (var thread in incognito)
        {
            documents.Add(ChatHistorySearch.CreateDocument(thread));
        }

        ct.ThrowIfCancellationRequested();
        return LexicalSearchStrategy.Search(trimmedQuery, documents, documents.Count);
    }

    public async Task<ChatThread?> GetThreadAsync(string userId, string threadId, CancellationToken ct = default,
        int? messageLimit = null, string? beforeMessageId = null, string? firstMessageId = null)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct,
            messageLimit, beforeMessageId, firstMessageId).ConfigureAwait(false);
        if (thread?.IsUnread == true &&
            await userScope.ChatThreadRepository
                .TryMarkThreadReadAsync(userId, thread.Id, ct)
                .ConfigureAwait(false))
        {
            thread.IsUnread = false;
            await NotifyThreadChangedAsync(userId).ConfigureAwait(false);
        }

        if (thread?.BranchOrigin is { } origin)
        {
            thread.SourceThreadAvailable = await userScope.ChatThreadRepository
                .ThreadExistsAsync(userId, origin.ParentThreadId, ct).ConfigureAwait(false);
        }

        if (thread is not null)
        {
            foreach (var message in thread.DisplayMessages)
            {
                if (message.ToolApproval is { Outcome: null } approval &&
                    !userScope.WebToolApprovalService.IsPending(approval.RequestId))
                {
                    approval.Outcome = ToolApprovalOutcome.Expired;
                }
            }
        }

        return thread;
    }

    /// <summary>Supplies a bounded display view while the runtime keeps its complete saved session.</summary>
    public async Task<ChatThreadViewResponse?> GetThreadViewAsync(string userId, string threadId,
        CancellationToken ct = default, string? beforeMessageId = null, string? firstMessageId = null)
    {
        var thread = await GetThreadAsync(userId, threadId, ct, messageLimit: 40,
            beforeMessageId, firstMessageId).ConfigureAwait(false);
        if (thread is null) { return null; }

        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var view = new ChatThreadViewResponse
        {
            Thread = thread, HasOlderMessages = thread.HasOlderDisplayMessages,
            HasSession = thread.SessionElement is not null,
            CanContinue = ChatThreadAgentSessionStore.GetRunOutcome(thread.SessionElement) == ChatRunOutcome.Stopped,
            ContinuationRecipientAgentId = ChatParticipantSessions.Read(thread.SessionElement).LastRecipientId,
            ModelProviderId = await scope.ConversationChoices.GetModelProviderIdAsync(userId, thread, ct).ConfigureAwait(false)
        };
        thread.SessionElement = null;
        thread.SessionCheckpoints = null;
        return view;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetSessionMessagesAsync(string userId, string threadId, CancellationToken ct = default)
    {
        var snapshot = await GetSessionInspectorAsync(userId, threadId, ct).ConfigureAwait(false);
        return snapshot.Messages;
    }

    /// <summary>Compacts the model session for one user-owned thread without changing its visible transcript.</summary>
    internal async Task<CompactThreadResult> CompactThreadAsync(
        string userId,
        string threadId,
        string? modelProviderId,
        CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        if (thread is null)
        {
            return CompactThreadResult.NotFound;
        }

        if (thread.SessionElement is null)
        {
            return CompactThreadResult.NoChange;
        }

        var agentDefinition = userScope.AppService.ResolveAgentDefinition(thread.AgentId);
        var session = await sessionStore
            .LoadForUserAsync(userId, threadId, modelProviderId, ct)
            .ConfigureAwait(false);
        if (!session.TryGetInMemoryChatHistory(out var history) || history.Count == 0)
        {
            return CompactThreadResult.NoChange;
        }

        var providerConfig = userScope.AppService.ResolveChatModelProvider(agentDefinition, modelProviderId);
        var runId = $"compact_{Guid.NewGuid():N}";
        var egressLedger = new EgressLedger();
        var usageLedger = new UsageLedger();
        var fallbackSignal = new FallbackActivationSignal();
        var runContext = new RunContext
        {
            UserId = userId,
            ExecutionKind = RunExecutionKind.WebChat,
            ExecutionId = runId,
            AirGapped = userScope.PrivacySettings.IsLocalOnly || agentDefinition.IsLocalOnly,
            EgressLedger = egressLedger,
            UsageLedger = usageLedger,
            FallbackSignal = fallbackSignal,
            CurrentAgentName = agentDefinition.Name,
            CurrentAgentId = agentDefinition.Id
        };

        await using var runScope = runContextAccessor.SetContext(runContext);
        try
        {
            var resolution = await userScope.AppService
                .GetChatClientResolutionAsync(agentDefinition, providerConfig)
                .ConfigureAwait(false);
            var chatClient = userScope.AppService.WrapWithEgressCapture(
                resolution.Client,
                agentDefinition.Id,
                resolution.EffectiveProvider);
            var reducer = await userScope.CrewService.CreateChatReducerAsync(
                    userId,
                    agentDefinition,
                    chatClient,
                    resolution.EffectiveProvider,
                    ct)
                .ConfigureAwait(false);
            var compacted = (await reducer.CompactAsync(history, ct).ConfigureAwait(false)).ToList();
            if (history.SequenceEqual(compacted, ReferenceEqualityComparer.Instance))
            {
                return CompactThreadResult.NoChange;
            }

            session.SetInMemoryChatHistory(compacted);
            await sessionStore.SaveForUserAsync(userId, threadId, session, modelProviderId, ct).ConfigureAwait(false);
            await NotifyThreadChangedAsync(userId).ConfigureAwait(false);
            return CompactThreadResult.Compacted;
        }
        finally
        {
            await FinalizeCompactionTelemetryAsync(
                    userScope,
                    thread,
                    runId,
                    runContext,
                    usageLedger,
                    egressLedger,
                    fallbackSignal)
                .ConfigureAwait(false);
        }
    }

    private async Task FinalizeCompactionTelemetryAsync(
        UserServiceScope userScope,
        ChatThread thread,
        string runId,
        RunContext runContext,
        UsageLedger usageLedger,
        EgressLedger egressLedger,
        FallbackActivationSignal fallbackSignal)
    {
        {
            try
            {
                var metricsEvent = MetricsEventBuilder.BuildEvent(
                    runContext,
                    "web_chat",
                    usageLedger.Snapshot(),
                    [],
                    fallbackSignal.Activated);
                await metricsEventStore.AppendAsync(userScope.UserId,
                    thread.IsIncognito ? MetricsEventBuilder.Anonymous(metricsEvent.Usage, MetricsEventBuilder.IncognitoBucket) : metricsEvent, CancellationToken.None).ConfigureAwait(false);
                await budgets.NotifyCompletedAsync(userScope.UserId, metricsEvent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to append metrics for chat compaction {RunId}.", runId);
            }
        }

        try
        {
            var auditLines = EgressAuditEventBuilder.BuildRunLines(
                runContext,
                egressLedger.Snapshot(),
                [],
                EgressAuditKinds.SourceChat,
                thread.IsIncognito ? null : thread.Id,
                thread.IsIncognito ? runId : null,
                null,
                DateTimeOffset.UtcNow);
            await egressAuditStore.AppendRangeAsync(userScope.UserId, auditLines, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append egress audit lines for chat compaction {RunId}.", runId);
        }
    }

    public async Task<ChatSessionInspectorSnapshot> GetSessionInspectorAsync(string userId, string threadId, CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        if (thread is null)
        {
            return new ChatSessionInspectorSnapshot();
        }

        var contextSnapshot = await GetUnredactedContextSnapshotAsync(
            userScope,
            thread,
            null,
            ct).ConfigureAwait(false);

        var labels = UserLabelMap.Create(userScope, agentCatalog);
        var egressEntries = (await egressAuditStore.ReadAllAsync(userId, ct).ConfigureAwait(false))
            .Where(record => string.Equals(record.ThreadId, threadId, StringComparison.Ordinal))
            .Select(record => labels.Label(record))
            .ToList();

        return new ChatSessionInspectorSnapshot
        {
            Messages = await userScope.RedactionService.RedactMessagesAsync(contextSnapshot.Messages, userId, ct).ConfigureAwait(false),
            SystemInstruction = thread.SystemInstruction is null ? null :
                await userScope.RedactionService.RedactTextAsync(thread.SystemInstruction, userId, ct).ConfigureAwait(false),
            EgressEntries = egressEntries
        };
    }

    /// <summary>
    /// Returns the unredacted model-session messages for server-side context accounting.
    /// </summary>
    internal async Task<ChatSessionContextSnapshot> GetUnredactedContextSnapshotAsync(
        UserServiceScope userScope,
        ChatThread thread,
        string? modelProviderId,
        CancellationToken ct = default)
    {
        if (thread.SessionElement is null)
        {
            return ChatSessionContextSnapshot.Empty;
        }

        try
        {
            var agentDefinition = userScope.AppService.ResolveAgentDefinition(thread.AgentId);
            var agent = await userScope.AppService.GetOrCreateAgent(
                userScope.UserId,
                agentDefinition,
                modelProviderId).ConfigureAwait(false);
            var session = await agent.DeserializeSessionAsync(thread.SessionElement.Value, cancellationToken: ct).ConfigureAwait(false);
            var messages = session.TryGetInMemoryChatHistory(out var history) ? history : [];
            return new ChatSessionContextSnapshot(messages, session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to deserialize chat session {ThreadId} for user {UserId} and agent {AgentId}.",
                thread.Id,
                userScope.UserId,
                thread.AgentId);
            return ChatSessionContextSnapshot.Empty;
        }
    }

    public async Task<IReadOnlyList<ChatTodoItem>> GetTodosAsync(
        string userId,
        string threadId,
        string? modelProviderId,
        CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        // Active runs publish working-list changes before their final session is saved.
        if (thread is not null && projectionStore.TryGetSnapshot(userId, threadId, out var snapshot) &&
            snapshot is { Phase: not ChatRunProjectionPhase.Terminal, Todos: { } todos })
        {
            return todos;
        }

        if (thread?.SessionElement is null)
        {
            return [];
        }

        ChatClientAgent agent;
        try
        {
            var agentDefinition = userScope.AppService.ResolveAgentDefinition(thread.AgentId);
            agent = await userScope.AppService.GetOrCreateAgent(
                userScope.UserId,
                agentDefinition,
                modelProviderId).ConfigureAwait(false);
        }
        catch (ModelProviderUnavailableException)
        {
            // Passive todo metadata must not make provider readiness a page-health dependency.
            return [];
        }

        var session = await agent.DeserializeSessionAsync(thread.SessionElement.Value, cancellationToken: ct).ConfigureAwait(false);

        using var reader = new TodoProvider();
        var items = await reader.GetAllTodosAsync(session).ConfigureAwait(false);
        return [.. items.Select(item => new ChatTodoItem(item.Id, item.Title, item.Description, item.IsComplete))];
    }

    /// <summary>Returns the delegated agent conversations recorded in one chat thread.</summary>
    public async Task<IReadOnlyList<AgentConversationView>> GetAgentConversationsAsync(
        string userId,
        string threadId,
        string? modelProviderId,
        CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        // A running root keeps worker progress in its live session until the run saves it.
        var live = steeringRegistry.Find(userId, threadId);
        if (thread is null || (thread.SessionElement is null && live?.DelegatedWork is null))
        {
            return [];
        }

        var definition = userScope.AppService.ResolveAgentDefinition(thread.AgentId);
        var reader = new AgentAsToolProvider(definition.AgentToolIds, userScope.CrewService.AIAgents);
        // Each participant keeps its own workers in its native session, nested in the owner's session.
        var sessions = new List<(string AgentId, JsonElement? State)> { (definition.Id, thread.SessionElement) };
        sessions.AddRange(ChatParticipantSessions.Read(thread.SessionElement).Participants
            .Where(participant => participant.AgentId != definition.Id)
            .Select(participant => (participant.AgentId, participant.Session)));
        if (live is not null && sessions.All(item => item.AgentId != live.AgentId))
        {
            sessions.Add((live.AgentId, null));
        }

        var conversations = new List<AgentToolConversation>();
        try
        {
            ChatClientAgent? agent = null;
            foreach (var (agentId, state) in sessions)
            {
                if (live is not null && agentId == live.AgentId && live.DelegatedWork is { } work)
                {
                    conversations.AddRange(await reader.GetConversationsAsync(live.Agent, live.Session, work, ct).ConfigureAwait(false));
                    continue;
                }

                if (state is not { } saved)
                {
                    continue;
                }

                try
                {
                    agent ??= await userScope.AppService.GetOrCreateAgent(
                        userScope.UserId,
                        definition,
                        modelProviderId).ConfigureAwait(false);
                }
                catch (ModelProviderUnavailableException)
                {
                    // Passive conversation metadata must not make provider readiness a page-health dependency.
                    break;
                }

                var session = await agent.DeserializeSessionAsync(saved, cancellationToken: ct).ConfigureAwait(false);
                conversations.AddRange(await reader.GetConversationsAsync(agent, session, cancellationToken: ct).ConfigureAwait(false));
            }

            // A consulted participant's workers stay in the root run until the consultation saves its session.
            foreach (var worker in live?.DelegatedWork?.GetWorkers() ?? [])
            {
                if (ReferenceEquals(worker.ParentSession, live!.Session))
                {
                    continue;
                }

                conversations.RemoveAll(item => item.Handle == worker.Handle);
                conversations.Add(new AgentToolConversation(worker.AgentId, worker.Name, worker.Handle,
                    worker.Status, worker.GetMessages()));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to read delegated agent conversations for chat session {ThreadId}, user {UserId}, and agent {AgentId}.",
                thread.Id,
                userScope.UserId,
                thread.AgentId);
            return [];
        }

        var views = new List<AgentConversationView>(conversations.Count);
        foreach (var conversation in conversations)
        {
            views.Add(new AgentConversationView
            {
                AgentId = conversation.AgentId,
                Name = conversation.Name,
                Handle = conversation.Handle,
                Status = conversation.Status,
                Messages = await userScope.RedactionService
                    .RedactMessagesAsync(conversation.Messages, userId, ct)
                    .ConfigureAwait(false)
            });
        }

        return views;
    }

    public async Task<ChatThread> CreateThreadAsync(string userId, string? agentId, string? projectId, CancellationToken ct = default)
        {
            var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
            var agentDefinition = userScope.AppService.ResolveAgentDefinition(agentId);

            if (projectId is not null) { await userScope.ProjectAccess.AuthorizeAsync(projectId, ct).ConfigureAwait(false); }

            await userScope.CurrentChatThreadService.StartNewConversationAsync(agentDefinition.Id, projectId, ct).ConfigureAwait(false);

            return new ChatThread
            {
                AgentId = agentDefinition.Id,
                AgentName = agentDefinition.Name,
                ProjectId = projectId
            };
        }

    /// <summary>
    /// Returns the current user's most recent resumable thread, if one is still inside the UI resume window.
    /// </summary>
    public async Task<ChatThread?> GetRecentCurrentThreadAsync(string userId, CancellationToken ct = default)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var recent = await scope.CurrentChatThreadService.GetRecentCurrentThreadAsync(ct).ConfigureAwait(false);
        return recent is null ? null : await GetThreadAsync(userId, recent.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Selects the recent chat from its latest exchange, then loads only its display page.</summary>
    public async Task<ChatThreadViewResponse?> GetRecentCurrentThreadViewAsync(string userId, CancellationToken ct = default)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var recent = await scope.CurrentChatThreadService.GetRecentCurrentThreadAsync(ct, messageLimit: 1).ConfigureAwait(false);
        return recent is null ? null : await GetThreadViewAsync(userId, recent.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Persists a new draft after its project and incognito state are selected.</summary>
    public Task CreateThreadAsync(string userId, ChatThread thread, CancellationToken ct = default)
        => SaveThreadCoreAsync(userId, thread, create: true, ct);

    public Task SaveThreadAsync(string userId, ChatThread thread, CancellationToken ct = default)
        => SaveThreadCoreAsync(userId, thread, create: false, ct);

    private async Task SaveThreadCoreAsync(string userId, ChatThread thread, bool create, CancellationToken ct)
        {
            var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
            var existingThread = await userScope.ChatThreadRepository.GetThreadAsync(userId, thread.Id, ct).ConfigureAwait(false);
            if (existingThread is not null && !string.Equals(existingThread.ProjectId, thread.ProjectId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A saved conversation cannot change project.");
            }

            var changingIncognito = existingThread is not null && existingThread.IsIncognito != thread.IsIncognito;
            if (changingIncognito && existingThread!.DisplayMessages.Count > 0)
            {
                throw new InvalidOperationException("Incognito mode cannot change after the first message.");
            }

            var draftPreset = changingIncognito
                ? await userScope.ConversationChoices.GetAsync(userId, existingThread!, ct).ConfigureAwait(false)
                : ChatPermissionPreset.UseAgentPolicy;
            if (thread.ProjectId is not null) { await userScope.ProjectAccess.AuthorizeAsync(thread.ProjectId, ct).ConfigureAwait(false); }

            var agentDefinition = userScope.AppService.ResolveAgentDefinition(thread.AgentId ?? existingThread?.AgentId);

            thread.AgentId = agentDefinition.Id;
            thread.AgentName = agentDefinition.Name;
            thread.IsUnread = false;
            // Browser saves cannot create or replace server-owned branch provenance or checkpoints.
            thread.BranchOrigin = existingThread?.BranchOrigin;
            thread.SessionCheckpoints = existingThread?.SessionCheckpoints ??
                new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
            thread.BranchableMessageIds = existingThread?.BranchableMessageIds ?? [];

            if (changingIncognito)
            {
                // A preset can save an empty draft, so its storage follows the first-message incognito choice.
                await userScope.ConversationChoices.DeleteAsync(userId, existingThread!, ct).ConfigureAwait(false);
                await userScope.ChatThreadRepository.DeleteThreadAsync(userId, thread.Id, ct).ConfigureAwait(false);
            }

            if (create || changingIncognito)
            {
                await userScope.ChatThreadRepository.CreateThreadAsync(userId, thread, ct: ct).ConfigureAwait(false);
            }
            else
            {
                await userScope.ChatThreadRepository.SaveThreadAsync(userId, thread, ct: ct).ConfigureAwait(false);
            }

            if (changingIncognito)
            {
                await userScope.ConversationChoices.SaveAsync(userId, thread, draftPreset, ct).ConfigureAwait(false);
            }

            await NotifyThreadChangedAsync(userId).ConfigureAwait(false);
        }

    public async Task<ChatThread?> BranchThreadAsync(string userId, string sourceThreadId,
            string messageId, CancellationToken ct = default)
        {
            var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
            var repository = scope.ChatThreadRepository;
            var source = await repository.GetThreadAsync(userId, sourceThreadId, ct).ConfigureAwait(false);
            if (source is null) { return null; }

            if (source.SessionCheckpoints is not { } retained ||
                !retained.TryGetValue(messageId, out var checkpointJson))
            {
                throw new InvalidOperationException("This reply has no saved context for branching.");
            }

            var checkpoint = System.Text.Json.JsonSerializer.Deserialize<ChatTurnCheckpoint>(checkpointJson)
                ?? throw new InvalidOperationException("The reply checkpoint cannot be read.");
            var cutoff = source.DisplayMessages.ToList().FindIndex(message => message.MessageId == messageId);
            if (cutoff < 0 || checkpoint.Thread.DisplayMessages.Count != cutoff + 1 ||
                checkpoint.Thread.DisplayMessages[^1].MessageId != messageId ||
                !source.DisplayMessages.Take(cutoff + 1).Select(message => message.MessageId)
                    .SequenceEqual(checkpoint.Thread.DisplayMessages.Select(message => message.MessageId), StringComparer.Ordinal))
            {
                throw new InvalidOperationException("The reply no longer matches its saved context.");
            }

            if (source.ProjectId is not null) { await scope.ProjectAccess.AuthorizeAsync(source.ProjectId, ct).ConfigureAwait(false); }

            var definition = scope.AppService.ResolveAgentDefinition(checkpoint.Thread.AgentId);
            if (definition.Id != checkpoint.Thread.AgentId)
            {
                throw new InvalidOperationException("The reply's original agent is unavailable.");
            }

            var rootId = source.BranchOrigin?.RootThreadId ?? source.Id;
            // The branch endpoint holds both source and family leases through the child save.
            var summaries = await repository.GetThreadsAsync(userId, ct).ConfigureAwait(false);
            var ephemeral = await repository.GetIncognitoThreadsAsync(userId, ct).ConfigureAwait(false);
            var family = summaries.Concat(ephemeral).Where(thread => thread.BranchOrigin?.RootThreadId == rootId)
                .Select(thread => thread.BranchOrigin!).ToArray();
            var number = family.Select(origin => origin.Number).DefaultIfEmpty(1).Max() + 1;
            var rootTitle = source.BranchOrigin?.RootTitle ??
                family.OrderBy(origin => origin.Number).FirstOrDefault()?.RootTitle ?? source.Title;
            var suffix = " (" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            var baseLength = Math.Min(rootTitle.Length, RenameThreadRequest.MaxTitleLength - suffix.Length);
            if (baseLength > 0 && char.IsHighSurrogate(rootTitle[baseLength - 1])) { baseLength--; }

            var child = System.Text.Json.JsonSerializer.Deserialize<ChatThread>(
                System.Text.Json.JsonSerializer.Serialize(checkpoint.Thread))
                ?? throw new InvalidOperationException("The captured thread cannot be copied.");
            child.Id = Guid.NewGuid().ToString();
            child.Title = rootTitle[..baseLength].TrimEnd() + suffix;
            child.AgentId = definition.Id;
            child.AgentName = definition.Name;
            child.IsUnread = false;
            child.IsCurrent = false;
            child.LastIndexedMessageCount = 0;
            child.LastSummarizedMessageCount = 0;
            child.LastSummarizedMessageOffset = 0;
            child.LastReflectedMessageCount = 0;
            child.LastReflectedMessageOffset = 0;
            var userMessageId = child.DisplayMessages.Last(message =>
                message.From == MessageOwner.User && !message.IsInteractionResponse).MessageId;
            child.BranchOrigin = new ChatBranchOrigin(source.Id, messageId, userMessageId, rootId, rootTitle, number);
            child.SourceThreadAvailable = true;

            try
            {
                var paths = await chatAttachmentService.CopyThreadFilesAsync(userId, source.Id, child,
                    checkpoint.ToolOutputFileNames, protector, ct).ConfigureAwait(false);
                child.DisplayMessages = Infrastructure.Chat.ChatAttachmentService
                    .RebindFilePaths(child.DisplayMessages.ToList(), paths);
                child.SessionElement = Infrastructure.Chat.ChatAttachmentService
                    .RebindFilePaths(child.SessionElement, paths);
                child.SystemInstruction = child.SystemInstruction is null ? null :
                    Infrastructure.Chat.ChatAttachmentService.RebindFilePaths(child.SystemInstruction, paths);
                var prefixIds = child.DisplayMessages.Select(message => message.MessageId).ToHashSet(StringComparer.Ordinal);
                child.SessionCheckpoints = retained.Where(pair => prefixIds.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => Infrastructure.Chat.ChatAttachmentService
                        .RebindFilePaths(pair.Value, paths), StringComparer.Ordinal);
                await repository.CreateThreadAsync(userId, child, ct: ct).ConfigureAwait(false);
            }
            catch
            {
                try { await repository.DeleteThreadAsync(userId, child.Id, CancellationToken.None).ConfigureAwait(false); }
                finally { chatAttachmentService.DeleteThreadFolder(userId, child.Id, child.IsIncognito); }

                throw;
            }

            await NotifyThreadChangedAsync(userId).ConfigureAwait(false);
            return child;
        }

    /// <summary>Renames one owned conversation without changing its current or unread state.</summary>
    public async Task<bool> RenameThreadAsync(
        string userId,
        string threadId,
        string title,
        CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        if (!await userScope.ChatThreadRepository
                .TryRenameThreadAsync(userId, threadId, title, ct)
                .ConfigureAwait(false))
        {
            return false;
        }

        await NotifyThreadChangedAsync(userId).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<ChatThread>> GetIncognitoThreadsAsync(string userId, CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        return await userScope.ChatThreadRepository.GetIncognitoThreadsAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task DiscardIncognitoThreadsAsync(string userId, CancellationToken ct = default)
    {
        if (!userServiceManager.TryGetExistingScope(userId, out var scope)) { return; }
        // Reuses the full per-thread teardown so shell containers and staged folders go with the thread.
        foreach (var thread in await scope.ChatThreadRepository.GetIncognitoThreadsAsync(userId, ct).ConfigureAwait(false))
        {
            try
            {
                await DeleteThreadAsync(userId, thread.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to discard incognito thread {ThreadId} for user {UserId}.", thread.Id, userId);
            }
        }
    }

    public async Task DeleteThreadAsync(string userId, string threadId, CancellationToken ct = default)
        {
            // Drain the active run before deletion so late completion cannot restore discarded chat state.
            threadRunGuard.Cancel(userId, threadId);
            using var deletionLease = await threadRunGuard.AcquireAsync(userId, threadId, $"delete_{Guid.NewGuid():N}", ct).ConfigureAwait(false);
            var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
            // The repository delete drops the in-memory thread, so read the flag before it goes.
            var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
            var incognito = thread?.IsIncognito == true;
            // Dependent stores use the canonical identity even when the route accepted a case alias.
            threadId = thread?.Id ?? threadId;
            await userScope.ChatThreadRepository.DeleteThreadAsync(userId, threadId, ct).ConfigureAwait(false);
            // Remove staged attachments alongside the thread; failures are logged inside the service.
            chatAttachmentService.DeleteThreadFolder(userId, threadId, incognito);
            // Tear down the conversation's shell container alongside the thread; thread deletion remains authoritative.
            try
            {
                try { await nodeBroker.CleanupExecutionAsync(userId, threadId, ct).ConfigureAwait(false); }
                finally
                {
                    await shellBackgroundJobProvider.CleanupExecutionAsync(threadId, ct).ConfigureAwait(false);
                    await shellExecutorProvider.EvictAsync(threadId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Deleted thread {ThreadId} for user {UserId}, but shell teardown failed.", threadId, userId);
            }

            // Primary thread deletion is authoritative; memory cleanup is best-effort follow-up work.
            try
            {
                await userScope.ChatMemoryService.DeleteThreadAsync(userId, threadId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Deleted thread {ThreadId} for user {UserId}, but chat memory cleanup failed.", threadId, userId);
            }

            await NotifyThreadChangedAsync(userId).ConfigureAwait(false);
            projectionStore.Remove(userId, threadId);
        }

    public async Task<bool> DeleteAllThreadsAsync(string userId, CancellationToken ct = default)
        {
            var succeeded = true;
            var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
            // GetThreadsAsync is persisted-only, so incognito shell containers need the second source.
            IReadOnlyList<ChatThread> threads =
            [
                .. await userScope.ChatThreadRepository.GetThreadsAsync(userId, ct: ct).ConfigureAwait(false),
                .. await userScope.ChatThreadRepository.GetIncognitoThreadsAsync(userId, ct).ConfigureAwait(false)
            ];
            await userScope.ChatThreadRepository.DeleteAllThreadsAsync(userId, ct).ConfigureAwait(false);
            // Remove every staged attachment folder for this user; failures are logged inside the service.
            succeeded &= chatAttachmentService.DeleteAllThreadFolders(userId);
            // Tear down shell work owned by deleted conversations; deletion itself remains authoritative.
            try
            {
                try
                {
                    await Task.WhenAll(threads.Select(thread => nodeBroker.CleanupExecutionAsync(userId, thread.Id, ct)))
                        .ConfigureAwait(false);
                }
                finally
                {
                    await shellBackgroundJobProvider.CleanupUserAsync(userId, ct).ConfigureAwait(false);
                    foreach (ChatThread thread in threads)
                    {
                        await shellExecutorProvider.EvictAsync(thread.Id).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                succeeded = false;
                logger.LogWarning(ex, "Deleted all threads for user {UserId}, but shell teardown failed.", userId);
            }

            // Bulk thread deletion is authoritative; memory cleanup is best-effort follow-up work.
            try
            {
                await userScope.ChatMemoryService.DeleteAllThreadsAsync(userId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                succeeded = false;
                logger.LogWarning(ex, "Deleted all threads for user {UserId}, but chat memory cleanup failed.", userId);
            }

            await NotifyThreadChangedAsync(userId).ConfigureAwait(false);
            return succeeded;
        }

    public async Task SubmitBooleanElicitationAsync(string userId, Guid requestId, bool? value, bool isCancel, CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        await userScope.WebElicitationService.SubmitBooleanElicitationAsync(requestId, value, isCancel).ConfigureAwait(false);
    }

    public async Task SubmitStringElicitationAsync(
        string userId,
        Guid requestId,
        IReadOnlyDictionary<string, string> values,
        bool isCancel,
        CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        await userScope.WebElicitationService.SubmitStringElicitationAsync(requestId, values, isCancel).ConfigureAwait(false);
    }

    public async Task SubmitNumberElicitationAsync(
        string userId,
        Guid requestId,
        IReadOnlyDictionary<string, NumberElicitationValue> values,
        bool isCancel,
        CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var payload = values.ToDictionary(
            static pair => pair.Key,
            static pair => (pair.Value.Value, pair.Value.IsInteger),
            StringComparer.OrdinalIgnoreCase);
        await userScope.WebElicitationService.SubmitNumberElicitationAsync(requestId, payload, isCancel).ConfigureAwait(false);
    }

    public async Task SubmitEnumElicitationAsync(
        string userId,
        Guid requestId,
        IReadOnlyDictionary<string, string> values,
        bool isCancel,
        CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        await userScope.WebElicitationService.SubmitEnumElicitationAsync(requestId, values, isCancel).ConfigureAwait(false);
    }

    public async Task SubmitToolApprovalAsync(string userId, string requestId, ToolApprovalDecision decision, CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        await userScope.WebToolApprovalService.SubmitToolApprovalAsync(requestId, decision).ConfigureAwait(false);
    }

    /// <summary>Reads current permissions from the retained runtime and encrypted settings.</summary>
    public async Task<ChatPermissions> GetPermissionsAsync(string userId, string threadId, CancellationToken ct)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Conversation is unavailable.");
        var machine = Environment.MachineName;
        var devices = await nodeCredentials.ListAsync(userId, ct).ConfigureAwait(false);
        if (thread.ProjectId is { } projectId)
        {
            var project = await scope.ProjectAccess.AuthorizeAsync(projectId, ct).ConfigureAwait(false);
            if (project.NodeId is { } nodeId)
            {
                machine = devices.FirstOrDefault(device => device.Id == nodeId)?.Name
                    ?? "Selected device (name unavailable)";
            }
        }

        var preset = await scope.ConversationChoices.GetAsync(userId, thread, ct).ConfigureAwait(false);
        projectionStore.TryGetSnapshot(userId, threadId, out var snapshot);
        var running = snapshot is { Phase: not ChatRunProjectionPhase.Terminal } ? snapshot.PermissionPreset : null;
        return new(preset, await permissions.IsOperatorAsync(userId, ct).ConfigureAwait(false), machine,
            scope.WebToolApprovalService.GetSessionPermissions(threadId).Select(grant =>
                grant.ExecutionDevice is { } device && devices.FirstOrDefault(node => node.Id == device) is { } node
                    ? grant with { ExecutionDevice = node.Name } : grant).ToArray(), running);
    }

    /// <summary>Saves an owned conversation preset after current operator authorization.</summary>
    public async Task<ChatPermissions> SavePermissionsAsync(string userId, string threadId, ChatPermissionPreset preset, CancellationToken ct)
    {
        await ConversationExecutionAccess.RequireAsync(permissions, userId, preset, ct).ConfigureAwait(false);
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Conversation is unavailable.");
        await scope.ConversationChoices.SaveAsync(userId, thread, preset, ct).ConfigureAwait(false);
        return await GetPermissionsAsync(userId, threadId, ct).ConfigureAwait(false);
    }

    /// <summary>Revokes future reusable approval decisions without interrupting approved work.</summary>
    public async Task RevokePermissionAsync(string userId, string threadId, string? grantId, CancellationToken ct)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        if (!await scope.ChatThreadRepository.ThreadExistsAsync(userId, threadId, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Conversation is unavailable.");
        }

        scope.WebToolApprovalService.RevokeSessionPermission(threadId, grantId);
    }

    private Task NotifyThreadChangedAsync(string userId)
    {
        return chatThreadNotifications.NotifyAsync(userId, new ChatThreadChanged());
    }
    /// <summary>Rechecks decrypted references and completes deferred cleanup when deletion can be confirmed.</summary>
    public async Task CleanupAgentReferencesAfterUnlockAsync(string unlockedUserId, CancellationToken ct)
    {
        // Personal-context storage lives only for this cleanup operation.
        using var cleanupScope = serviceScopeFactory.CreateScope();
        var contexts = cleanupScope.ServiceProvider.GetRequiredService<Oscova.Agents.Definitions.ISharedAgentPersonalContextRepository>();
        await agentCatalog.ReloadAsync(ct).ConfigureAwait(false);
        var ownersLoaded = !Directory.EnumerateDirectories(Infrastructure.Utils.PathUtil.UsersPath)
            .Any(path => Guid.TryParse(Path.GetFileName(path), out _) &&
                Directory.Exists(Path.Combine(path, Core.UserFileNames.AgentsDirectory)) &&
                !agentCatalog.IsOwnerFullyLoaded(Path.GetFileName(path)));

        foreach (var userId in userServiceManager.ActiveUserIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!protector.IsAvailable(userId) || pendingDuress.Contains(userId) ||
                !userServiceManager.TryGetExistingScope(userId, out var scope) || !scope.AppService.IsInitialized)
            {
                continue;
            }

            var threads = await scope.ChatThreadRepository.GetThreadsAsync(userId, ct: ct).ConfigureAwait(false);
            var tasks = await scope.AgentTaskRepository.GetTasksByUserAsync(userId, ct).ConfigureAwait(false);
            await scope.AgentGoals.LoadAsync().ConfigureAwait(false);
            if (string.Equals(userId, unlockedUserId, StringComparison.Ordinal))
            {
                // Recheck tool access when this consumer unlocks, preserving references to locked owners.
                var toolIds = scope.AppService.CustomAgentDefinitions
                    .Where(agent => string.Equals(agent.OwnerUserId, userId, StringComparison.Ordinal))
                    .Prepend(scope.AppService.MainAgentDefinition)
                    .SelectMany(agent => agent.AgentToolIds).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var toolId in toolIds)
                {
                    var ownerId = agentCatalog.TryGetOwnerUserId(toolId);
                    if (ownerId is null || !protector.IsAvailable(ownerId)) { continue; }

                    var accessible = await scope.AgentDefinitionStore.GetAgentAsync(userId, toolId, ct).ConfigureAwait(false);
                    if (accessible?.IsExposedAsTool != true)
                    {
                        await scope.AppService.RemoveAgentToolReferencesAsync(toolId).ConfigureAwait(false);
                    }
                }
            }

            var references = threads.Select(thread => thread.AgentId)
                .Concat(tasks.Select(task => task.AgentId))
                .Concat(scope.AgentGoals.Limits.Keys)
                .Concat(scope.AppService.CustomAgentDefinitions
                    .Where(agent => string.Equals(agent.OwnerUserId, userId, StringComparison.Ordinal))
                    .Prepend(scope.AppService.MainAgentDefinition)
                    .SelectMany(agent => agent.AgentToolIds));
            var deletedIds = references
                .Where(id => !string.IsNullOrWhiteSpace(id) && ownersLoaded && agentCatalog.TryGetOwnerUserId(id) is null)
                .Select(id => id!)
                .ToHashSet(StringComparer.Ordinal);

            // An absent entry is inconclusive while another owner's definitions remain encrypted.
            await contexts.DeleteWhereAsync(userId, async (agentId, ownerId) =>
            {
                if (agentCatalog.TryGetOwnerUserId(agentId) is null)
                {
                    if (ownersLoaded || agentCatalog.IsOwnerFullyLoaded(ownerId)) { deletedIds.Add(agentId); }

                    return false;
                }

                if (!protector.IsAvailable(ownerId)) { return false; }

                var accessible = await scope.AgentDefinitionStore.GetAgentAsync(userId, agentId, ct).ConfigureAwait(false);
                return accessible?.IsShared != true;
            }, ct).ConfigureAwait(false);

            if (deletedIds.Count == 0) { continue; }

            var threadIds = threads.Where(thread => thread.AgentId is not null && deletedIds.Contains(thread.AgentId))
                .Select(thread => thread.Id).ToArray();
            using var lease = await threadRunGuard.CancelAndAcquireUserAsync(userId, ct, threadIds).ConfigureAwait(false);
            foreach (var id in deletedIds)
            {
                await scope.AppService.ClearAgentReferencesAsync(new Oscova.Agents.Definitions.AgentDefinition { Id = id })
                    .ConfigureAwait(false);
            }

            await NotifyThreadChangedAsync(userId).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Carries unredacted model-session state for server-only context accounting.
/// </summary>
internal sealed record ChatSessionContextSnapshot(IReadOnlyList<ChatMessage> Messages, AgentSession? Session)
{
    public static ChatSessionContextSnapshot Empty { get; } = new([], null);
}

#pragma warning restore MAAI001
