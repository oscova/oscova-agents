using AGUI.Server;
using AGUI.Abstractions;
using Microsoft.Agents.AI;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Hooks;
using Oscova.Agents.Host.Server.Agents;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Metrics;
using Oscova.Agents.Host.Services;
using System.Text.Json;

namespace Oscova.Agents.Host.Server.Chat;

/// <summary>Owns accepted chat tasks independently of their initiating HTTP requests.</summary>
public sealed class ChatRunHost(
    ChatDispatchAgent dispatchAgent,
    ChatThreadAgentSessionStore sessionStore,
    UserServiceManager userServiceManager,
    ThreadRunGuard threadRunGuard,
    ChatRunProjectionStore projectionStore,
    ModelBudgetService budgets,
    ILogger<ChatRunHost> logger) : IHostedService, IDisposable
{
    private readonly Lock _executionLifetimeGate = new();
    private readonly HashSet<Task> _ownedExecutions = [];
    // Queue identity matches the run guard: exact user and case-insensitive thread, under the lifetime gate.
    private readonly Dictionary<(string UserId, string ThreadId), List<QueuedFollowUp>> _followUps = new(
        EqualityComparer<(string UserId, string ThreadId)>.Create(
            (left, right) => StringComparer.Ordinal.Equals(left.UserId, right.UserId)
                && StringComparer.OrdinalIgnoreCase.Equals(left.ThreadId, right.ThreadId),
            key => HashCode.Combine(StringComparer.Ordinal.GetHashCode(key.UserId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.ThreadId))));
    private readonly CancellationTokenSource _executionLifetimeCancellation = new();
    private bool _isStopping;
    private int _disposed;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Admits one run and hands its pre-created viewer stream to the endpoint.</summary>
    internal ChatRunStream? TryStartAttached(
        string userId,
        ChatRequestContext context,
        CancellationToken admissionCancellation)
    {
        return TryStart(
            userId,
            context,
            admissionCancellation,
            createViewer: true,
            requiresExistingThread: false,
            out var stream)
            ? stream
            : null;
    }

    /// <summary>Admits one detached web run without creating a viewer stream.</summary>
    internal bool TryStartDetached(
        string userId,
        ChatRequestContext context,
        CancellationToken admissionCancellation) =>
        TryStart(
            userId,
            context,
            admissionCancellation,
            createViewer: false,
            requiresExistingThread: true,
            out _, budgetAdmitted: true);

    /// <summary>Transfers a saved-state validation lease into accepted execution.</summary>
    internal bool TryStartDetached(string userId, ChatRequestContext context,
        CancellationToken admissionCancellation, ThreadRunGuard.RunLease lease, UserQuestionAnswer? answer) =>
        TryStart(userId, context, admissionCancellation, false, true, out _, lease, answer, budgetAdmitted: true);

    /// <summary>Starts the follow-up at once when the thread is idle, otherwise queues it behind the live run.</summary>
    internal bool? EnqueueOrStart(string userId, QueuedFollowUp item, bool atFront, CancellationToken admissionCancellation)
    {
        lock (_executionLifetimeGate)
        {
            if (TryStart(userId, item.Context, admissionCancellation, createViewer: false, requiresExistingThread: true, out _))
            {
                return true;
            }

            var key = (userId, item.Context.Input.ThreadId);
            // Only a live host run can advance accepted work when it completes.
            if (_isStopping || admissionCancellation.IsCancellationRequested ||
                !projectionStore.TryGetSnapshot(userId, key.ThreadId, out var active) ||
                active is null || active.Phase == ChatRunProjectionPhase.Terminal ||
                threadRunGuard.GetActiveRunId(userId, key.ThreadId) != active.RunId)
            {
                return null;
            }

            if (!_followUps.TryGetValue(key, out var items))
            {
                items = [];
                _followUps[key] = items;
            }

            items.Insert(atFront ? 0 : items.Count, item);
            return false;
        }
    }

    /// <summary>Lists a thread's queued follow-ups in run order.</summary>
    internal IReadOnlyList<QueuedFollowUp> GetFollowUps(string userId, string threadId)
    {
        lock (_executionLifetimeGate)
        {
            return _followUps.TryGetValue((userId, threadId), out var items) ? [.. items] : [];
        }
    }

    /// <summary>Takes one queued follow-up out of its thread's queue.</summary>
    internal QueuedFollowUp? TryRemoveFollowUp(string userId, string threadId, string messageId) =>
        TryTakeFollowUp(userId, threadId, messageId)?.Item;

    /// <summary>Transfers ownership of a queued item together with its position.</summary>
    internal (QueuedFollowUp Item, int Index)? TryTakeFollowUp(string userId, string threadId, string messageId)
    {
        lock (_executionLifetimeGate)
        {
            if (!_followUps.TryGetValue((userId, threadId), out var items))
            {
                return null;
            }

            var index = items.FindIndex(item => string.Equals(item.MessageId, messageId, StringComparison.Ordinal));
            if (index < 0)
            {
                return null;
            }

            var removed = items[index];
            items.RemoveAt(index);
            return (removed, index);
        }
    }

    /// <summary>Restores an unaccepted transfer without starting a run.</summary>
    internal void RestoreFollowUp(string userId, QueuedFollowUp item, int index)
    {
        lock (_executionLifetimeGate)
        {
            var items = _followUps[(userId, item.Context.Input.ThreadId)];
            items.Insert(Math.Min(index, items.Count), item);
        }
    }

    // Runs the next queued follow-up after a turn on its thread completes normally.
    private bool StartNextFollowUp(string userId, string threadId)
    {
        lock (_executionLifetimeGate)
        {
            if (_followUps.TryGetValue((userId, threadId), out var items) &&
                items.Count > 0 &&
                TryStart(userId, items[0].Context, CancellationToken.None, createViewer: false, requiresExistingThread: true, out _))
            {
                items.RemoveAt(0);
                return true;
            }

            return false;
        }
    }

    private async Task ContinueAfterTitleAsync(string userId, string threadId)
    {
        lock (_executionLifetimeGate)
        {
            // A delayed title must never resume work paused by Stop or displace a newer live turn.
            if (!projectionStore.TryGetSnapshot(userId, threadId, out var latest) ||
                latest?.Outcome != ChatRunOutcome.Completed || latest.Phase != ChatRunProjectionPhase.Terminal ||
                StartNextFollowUp(userId, threadId))
            {
                return;
            }
        }

        await TryStartGoalAsync(userId, threadId).ConfigureAwait(false);
    }

    /// <summary>Admits goal pursuit only when ordinary queued work and interactions have settled.</summary>
    internal async Task<bool> TryStartGoalAsync(string userId, string threadId, CancellationToken ct = default)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        if (thread is null || threadRunGuard.GetActiveRunId(userId, threadId) is not null) { return false; }

        var goal = await scope.ConversationGoals.GetAsync(userId, thread, ct).ConfigureAwait(false);
        if (goal?.Status != ChatGoalStatus.Active || goal.RunId is not null) { return false; }

        if (thread.DisplayMessages.Any(message => message.Segments.Any(segment =>
            UserQuestions.TryRead(message, segment, out var card) && UserQuestions.GetState(thread, card!) == UserQuestions.CardState.Pending)))
        {
            await scope.ConversationGoals.UpdateAsync(userId, thread, current =>
                current?.Id == goal.Id && current.Status == ChatGoalStatus.Active
                    ? current.WithStatus(ChatGoalStatus.Blocked, "Waiting for your answer") : current, ct).ConfigureAwait(false);
            return false;
        }

        if (GetFollowUps(userId, threadId).Count > 0) { return StartNextFollowUp(userId, threadId); }

        await scope.AgentGoals.LoadAsync().ConfigureAwait(false);
        var limit = scope.AgentGoals.GetLimit(goal.AgentId!) ?? int.MaxValue;
        var runId = $"run_{Guid.NewGuid():N}";
        goal = await scope.ConversationGoals.UpdateAsync(userId, thread,
            current => current?.Id == goal.Id && current.Status == ChatGoalStatus.Active && current.RunId is null
                ? current with { RunId = runId, MaxIterations = limit } : current, ct).ConfigureAwait(false);
        if (goal?.RunId != runId) { return false; }

        var input = new RunAgentInput
        {
            ThreadId = threadId, RunId = runId,
            Messages = [new AGUIUserMessage { Id = $"goal_{Guid.NewGuid():N}", Content = goal.Objective }],
            ForwardedProperties = JsonSerializer.SerializeToElement(new
            {
                agentId = goal.AgentId, modelProviderId = goal.ModelProviderId
            })
        };
        var accepted = TryStart(userId, input.ToChatRequestContext(new(JsonSerializerDefaults.Web)),
            ct, false, true, out _, goal: goal);
        if (!accepted)
        {
            await scope.ConversationGoals.UpdateAsync(userId, thread,
                current => current?.Id == goal.Id && current.RunId == runId
                    ? current with { RunId = null } : current, CancellationToken.None).ConfigureAwait(false);
        }

        return accepted;
    }

    private bool TryStart(
        string userId,
        ChatRequestContext context,
        CancellationToken admissionCancellation,
        bool createViewer,
        bool requiresExistingThread,
        out ChatRunStream? stream,
        ThreadRunGuard.RunLease? admittedLease = null,
        UserQuestionAnswer? answer = null,
        bool budgetAdmitted = false,
        ChatGoal? goal = null)
    {
        lock (_executionLifetimeGate)
        {
            stream = null;
            if (_isStopping || admissionCancellation.IsCancellationRequested)
            {
                return false;
            }

            var threadId = context.Input.ThreadId;
            var runId = context.Input.RunId;
            var guard = admittedLease ?? threadRunGuard.Acquire(userId, threadId, runId);
            if (guard is null)
            {
                return false;
            }

            var userMessageId = context.Input.Messages?
                .OfType<AGUI.Abstractions.AGUIUserMessage>()
                .Select(message => message.Id)
                .LastOrDefault(id => !string.IsNullOrWhiteSpace(id));
            var projection = projectionStore.Begin(userId, threadId, runId, userMessageId);
            stream = createViewer ? new ChatRunStream() : null;
            CancellationTokenRegistration admissionRegistration = default;
            Task executionTask;
            try
            {
                // Until task tracking completes, a disconnected initiator still cancels this exact admission.
                admissionRegistration = admissionCancellation.Register(
                    () => threadRunGuard.CancelExact(userId, threadId, runId));
                var run = new ChatRunExecution(userId, context, guard, projection, stream, requiresExistingThread, answer, budgetAdmitted, goal);
                executionTask = Task.Run(() => RunAsync(run), CancellationToken.None);
            }
            catch (Exception ex)
            {
                projectionStore.Remove(userId, threadId);
                stream?.Complete(ex);
                admissionRegistration.Dispose();
                if (admittedLease is null) guard.Dispose();
                throw;
            }

            _ownedExecutions.Add(executionTask);
            _ = executionTask.ContinueWith(
                completedTask =>
                {
                    lock (_executionLifetimeGate)
                    {
                        _ownedExecutions.Remove(completedTask);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            admissionRegistration.Dispose();
            projection.AnnounceStarting();
            return true;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] ownedExecutions;
        lock (_executionLifetimeGate)
        {
            _isStopping = true;
            ownedExecutions = [.. _ownedExecutions];
        }

        // Dispose already cancelled the owned executions and released the token source.
        if (Volatile.Read(ref _disposed) == 0)
        {
            _executionLifetimeCancellation.Cancel();
        }

        await Task.WhenAll(ownedExecutions)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _executionLifetimeCancellation.Cancel();
        _executionLifetimeCancellation.Dispose();
    }

    private async Task RunAsync(ChatRunExecution run)
    {
        var outcome = ChatRunOutcome.Completed;
        Exception? failure = null;
        // Keep stop requests connected through session saving and terminal publication.
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _executionLifetimeCancellation.Token,
            run.Guard.CancellationToken);
        using var configuration = new Oscova.Agents.Core.Auth.ExecutionPermissionRegistry.ConfigurationScope(run.UserId, runCancellation.Token);
        var runToken = configuration.Token;
        AgentSession? session = null;
        AgentSession? precedingConsultationSession = null;
        var previousOutcome = ChatRunOutcome.None;
        // Session reconstruction must use the same composer provider as dispatch.
        var forwarded = run.Context.Input.ForwardedProperties ?? default;
        var modelProviderId = forwarded.ValueKind == JsonValueKind.Object &&
            forwarded.TryGetProperty("modelProviderId", out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        var continueSession = forwarded.ValueKind == JsonValueKind.Object &&
            forwarded.TryGetProperty("continueSession", out var continueProperty) &&
            continueProperty.ValueKind == JsonValueKind.True;
        try
        {
            if (!run.BudgetAdmitted)
            {
                var scope = await userServiceManager.GetOrCreateScopeAsync(run.UserId, runToken).ConfigureAwait(false);
                var thread = await scope.ChatThreadRepository.GetThreadAsync(run.UserId, run.Context.Input.ThreadId, runToken).ConfigureAwait(false);
                var requestedAgent = forwarded.ValueKind == JsonValueKind.Object && forwarded.TryGetProperty("agentId", out var agentProperty)
                    && agentProperty.ValueKind == JsonValueKind.String ? agentProperty.GetString() : null;
                var definition = scope.AppService.ResolveAgentDefinition(AgentSelectionResolver.ResolveAgentId(requestedAgent,
                    thread?.AgentId, thread?.DisplayMessages.Count > 0));
                var recipientId = forwarded.ValueKind == JsonValueKind.Object && forwarded.TryGetProperty("recipientAgentId", out var recipientValue)
                    && recipientValue.ValueKind == JsonValueKind.String ? recipientValue.GetString() : null;
                if (continueSession) { recipientId = ChatParticipantSessions.Read(thread?.SessionElement).LastRecipientId; }

                var owner = definition;
                var ownerRequiresLocalOnly = definition.IsLocalOnly;
                definition = await AgentSelectionResolver.ResolveRecipientAsync(scope, thread ?? new ChatThread(), definition, recipientId, runToken).ConfigureAwait(false);
                var providerId = AgentSelectionResolver.ResolveModelProviderId(owner, definition, modelProviderId,
                    scope.ModelProviderSettings.SelectedChatModelProviderId?.ToString());
                var selected = scope.AppService.ResolveChatModelProvider(definition, providerId);
                var resolution = await scope.AppService.GetChatClientResolutionAsync(definition, selected, ownerRequiresLocalOnly).ConfigureAwait(false);
                if (!await budgets.AdmitAsync(run.UserId, resolution.EffectiveProvider.Id, ct: runToken).ConfigureAwait(false))
                {
                    if (run.Goal is { } deniedGoal && thread is not null)
                    {
                        await scope.ConversationGoals.UpdateAsync(run.UserId, thread, current =>
                            current?.Id == deniedGoal.Id && current.RunId == run.Context.Input.RunId
                                ? current.WithStatus(ChatGoalStatus.Paused, "Budget reached") : current,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    // Queued work has no active confirmation dialog. Leave history untouched and notify its owner.
                    outcome = ChatRunOutcome.Stopped;
                    await budgets.NotifyDeniedAsync(run.UserId, resolution.EffectiveProvider.Id, runToken).ConfigureAwait(false);
                    return;
                }
            }

            if (run.RequiresExistingThread)
            {
                var scope = await userServiceManager
                    .GetOrCreateScopeAsync(run.UserId, runToken).ConfigureAwait(false);
                var thread = await scope.ChatThreadRepository
                    .GetThreadAsync(run.UserId, run.Context.Input.ThreadId, runToken)
                    .ConfigureAwait(false);
                if (thread is null)
                {
                    // A pending web run must not restore a conversation deleted before admission.
                    outcome = ChatRunOutcome.Failed;
                    return;
                }
            }

            session = await sessionStore.LoadForUserAsync(
                run.UserId,
                run.Context.Input.ThreadId,
                modelProviderId,
                runToken).ConfigureAwait(false);
            previousOutcome = ChatThreadAgentSessionStore.GetRunOutcome(session);
            if (continueSession &&
                (run.Stream is not null ||
                 ChatThreadAgentSessionStore.GetRunOutcome(session) != ChatRunOutcome.Stopped ||
                 !session.TryGetInMemoryChatHistory(out var savedHistory) ||
                 !savedHistory.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)))
            {
                throw new InvalidOperationException("The stopped session could not be restored for continuation.");
            }

            var regenerate = forwarded.ValueKind == JsonValueKind.Object &&
                forwarded.TryGetProperty("regenerateAssistantMessageId", out var regenerateValue) &&
                regenerateValue.ValueKind == JsonValueKind.String;
            var consultationTurn = ChatParticipantSessions.Load(session).Participants.Count > 1 ||
                (forwarded.ValueKind == JsonValueKind.Object &&
                 forwarded.TryGetProperty("recipientAgentId", out var recipientProperty) &&
                 recipientProperty.ValueKind == JsonValueKind.String &&
                 ChatParticipantSessions.Load(session).Find(recipientProperty.GetString()!) is null);
            if (consultationTurn && !continueSession)
            {
                precedingConsultationSession = session;
                session = await sessionStore.PrepareConsultationTurnAsync(run.UserId, run.Context.Input.ThreadId,
                    modelProviderId, session, regenerate, run.Projection.UserMessageId, runToken).ConfigureAwait(false);
            }

            if (ChatThreadAgentSessionStore.GetRunOutcome(session) != ChatRunOutcome.None)
            {
                // Dispatch reloads the cleared session before any transcript progress is saved.
                ChatThreadAgentSessionStore.SetRunOutcome(session, ChatRunOutcome.None);
                await sessionStore.SaveForUserAsync(
                    run.UserId, run.Context.Input.ThreadId, session, modelProviderId,
                    CancellationToken.None).ConfigureAwait(false);
            }

            var options = new ChatDispatchRunOptions
            {
                UserId = run.UserId,
                ChatOptions = run.Context.ChatOptions,
                AllowUserQuestions = run.Stream is null,
                ContinueSession = continueSession,
                SessionRewound = consultationTurn && regenerate,
                QuestionAnswer = run.QuestionAnswer,
                Goal = run.Goal,
                HasQueuedWork = () => GetFollowUps(run.UserId, run.Context.Input.ThreadId).Count > 0,
                OnTitleGenerationCompleted = () => ContinueAfterTitleAsync(run.UserId, run.Context.Input.ThreadId),
                Projection = run.Projection,
                ConfigurationScope = configuration
            };

            await foreach (var update in dispatchAgent.RunStreamingAsync(
                               run.Context.Messages,
                               session,
                               options,
                               runToken).ConfigureAwait(false))
            {
                run.Stream?.Publish(update);
            }
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            outcome = ChatRunOutcome.Stopped;
        }
        catch (HookDeniedException ex)
        {
            outcome = ChatRunOutcome.Failed;
            failure = ex;
            logger.LogInformation(
                ex,
                "Chat run {RunId} on thread {ThreadId} was denied by hook event {EventKind}.",
                run.Context.Input.RunId,
                run.Context.Input.ThreadId,
                ex.EventKind);
        }
        catch (Exception ex)
        {
            outcome = ChatRunOutcome.Failed;
            failure = ex;
            logger.LogError(
                ex,
                "Chat run {RunId} on thread {ThreadId} failed.",
                run.Context.Input.RunId,
                run.Context.Input.ThreadId);
        }
        finally
        {
            if (run.Goal is { } goal)
            {
                try
                {
                    var scope = await userServiceManager.GetOrCreateScopeAsync(run.UserId).ConfigureAwait(false);
                    var thread = await scope.ChatThreadRepository.GetThreadAsync(run.UserId, run.Context.Input.ThreadId).ConfigureAwait(false);
                    if (thread is not null)
                    {
                        await scope.ConversationGoals.UpdateAsync(run.UserId, thread, current =>
                        {
                            if (current?.Id != goal.Id || current.Status != ChatGoalStatus.Active || current.RunId != run.Context.Input.RunId) { return current; }

                            if (outcome != ChatRunOutcome.Completed || runToken.IsCancellationRequested)
                            {
                                return current.WithStatus(ChatGoalStatus.Paused,
                                    runToken.IsCancellationRequested ? "Stopped"
                                        : failure is ModelProviderUnavailableException or HttpRequestException ? "Provider unavailable" : "Couldn't continue");
                            }

                            return current.WithStatus(ChatGoalStatus.Active);
                        }, CancellationToken.None).ConfigureAwait(false);
                    }

                    if (failure is ModelProviderUnavailableException or HttpRequestException)
                    {
                        outcome = ChatRunOutcome.Completed;
                        failure = null;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not settle goal state for run {RunId} on thread {ThreadId}.", run.Context.Input.RunId, run.Context.Input.ThreadId);
                    outcome = ChatRunOutcome.Failed;
                    failure ??= ex;
                }
            }

            var hasPreparedInput = projectionStore.TryGetSnapshot(
                run.UserId, run.Context.Input.ThreadId, out var snapshot) && snapshot?.RunMessages is not null;
            // A rejected or canceled admission did not replace the last visible user turn.
            if (!hasPreparedInput && precedingConsultationSession is not null) { session = precedingConsultationSession; }

            var completedReplyId = snapshot?.RunId == run.Context.Input.RunId
                ? snapshot.RunMessages?.LastOrDefault(message => message.From == MessageOwner.Bot)?.MessageId
                : null;
            // An admission stopped before prepared input keeps the preceding saved turn eligible.
            ChatRunOutcome OutcomeToSave() => outcome == ChatRunOutcome.Stopped && !hasPreparedInput
                ? previousOutcome
                : outcome;
            if (session is not null)
            {
                try
                {
                    if (outcome != ChatRunOutcome.Failed && runToken.IsCancellationRequested)
                    {
                        outcome = ChatRunOutcome.Stopped;
                    }

                    ChatThreadAgentSessionStore.SetRunOutcome(session, OutcomeToSave());
                    await sessionStore.SaveForUserAsync(
                        run.UserId,
                        run.Context.Input.ThreadId,
                        session,
                        modelProviderId,
                        CancellationToken.None).ConfigureAwait(false);
                    if (outcome == ChatRunOutcome.Completed && !runToken.IsCancellationRequested)
                    {
                        await FinalizeQuestionCardsAsync(run).ConfigureAwait(false);
                    }

                    if (outcome == ChatRunOutcome.Completed && !runToken.IsCancellationRequested &&
                        completedReplyId is not null)
                    {
                        // Capture after question cards settle and before completion releases the source run guard.
                        await sessionStore.CaptureCheckpointForUserAsync(run.UserId,
                            run.Context.Input.ThreadId, completedReplyId, CancellationToken.None).ConfigureAwait(false);
                    }

                    if (outcome != ChatRunOutcome.Failed && runToken.IsCancellationRequested &&
                        outcome != ChatRunOutcome.Stopped)
                    {
                        // Stop can arrive during final persistence. Remove its checkpoint before publishing the result.
                        outcome = ChatRunOutcome.Stopped;
                        ChatThreadAgentSessionStore.SetRunOutcome(session, OutcomeToSave());
                        await sessionStore.SaveForUserAsync(
                            run.UserId, run.Context.Input.ThreadId, session, modelProviderId,
                            CancellationToken.None).ConfigureAwait(false);
                        if (completedReplyId is not null)
                        {
                            await sessionStore.DiscardCheckpointForUserAsync(run.UserId,
                                run.Context.Input.ThreadId, completedReplyId, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    outcome = ChatRunOutcome.Failed;
                    failure ??= ex;
                    logger.LogError(
                        ex,
                        "Failed to persist chat run {RunId} on thread {ThreadId} for user {UserId}.",
                        run.Context.Input.RunId,
                        run.Context.Input.ThreadId,
                        run.UserId);
                }
            }

            if (session is null && outcome != ChatRunOutcome.Failed && runToken.IsCancellationRequested)
            {
                outcome = ChatRunOutcome.Stopped;
            }

            if (outcome == ChatRunOutcome.Failed)
            {
                try
                {
                    var scope = await userServiceManager.GetOrCreateScopeAsync(run.UserId).ConfigureAwait(false);
                    var thread = await scope.ChatThreadRepository.GetThreadAsync(run.UserId, run.Context.Input.ThreadId).ConfigureAwait(false);
                    if (thread is not null)
                    {
                        // Failed queued work or final persistence leaves pursuit waiting for explicit recovery.
                        await scope.ConversationGoals.UpdateAsync(run.UserId, thread, current =>
                            current?.Status == ChatGoalStatus.Active && (current.RunId is null || current.RunId == run.Context.Input.RunId)
                                ? current.WithStatus(ChatGoalStatus.Paused,
                                    failure is ModelProviderUnavailableException or HttpRequestException ? "Provider unavailable" : "Couldn't continue")
                                : current, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not pause goal after failed run {RunId} on thread {ThreadId}.",
                        run.Context.Input.RunId, run.Context.Input.ThreadId);
                }
            }

            // The detached panel receives only the known safe failure category, never raw exception details.
            run.Projection.Complete(outcome, failure is NodeUnavailableException,
                failure as Oscova.Agents.Core.Mcp.McpConnectionException, failure is ProjectFolderUnavailableException);
            run.Guard.Dispose();
            // Explicit Resume can arrive before a canceled worker releases the thread.
            if ((outcome == ChatRunOutcome.Completed && !StartNextFollowUp(run.UserId, run.Context.Input.ThreadId))
                || outcome == ChatRunOutcome.Stopped)
            {
                try
                {
                    await TryStartGoalAsync(run.UserId, run.Context.Input.ThreadId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not continue goal on thread {ThreadId}.", run.Context.Input.ThreadId);
                }
            }

            run.Stream?.Complete(failure);
        }
    }

    // Saved display state becomes actionable only after the model session is safely persisted.
    private async Task FinalizeQuestionCardsAsync(ChatRunExecution run)
    {
        if (!projectionStore.TryGetSnapshot(run.UserId, run.Context.Input.ThreadId, out var snapshot) ||
            snapshot?.RunMessages is null || snapshot.RunId != run.Context.Input.RunId ||
            !snapshot.RunMessages.Any(message => message.Segments.Any(segment => UserQuestions.TryRead(message, segment, out _))))
            return;

        var scope = await userServiceManager.GetOrCreateScopeAsync(run.UserId).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(run.UserId, run.Context.Input.ThreadId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The question conversation no longer exists.");
        var slice = new List<ChatDisplayMessage>();
        foreach (var projected in snapshot.RunMessages)
        {
            var message = thread.DisplayMessages.FirstOrDefault(message => message.MessageId == projected.MessageId)
                ?? throw new InvalidOperationException("The question message was not persisted.");
            var finalized = UserQuestions.Finalize(message);
            if (!ReferenceEquals(finalized, message) &&
                !await scope.ChatThreadRepository.TryReplaceDisplayMessageAsync(run.UserId, thread.Id, finalized).ConfigureAwait(false))
                throw new InvalidOperationException("The question message could not be finalized.");
            slice.Add(finalized);
        }

        run.Projection.Publish(slice, snapshot.Phase, ChatRunChangeKind.Structural);
    }

    private sealed record ChatRunExecution(
        string UserId,
        ChatRequestContext Context,
        ThreadRunGuard.RunLease Guard,
        ChatRunProjection Projection,
        ChatRunStream? Stream,
        bool RequiresExistingThread,
        UserQuestionAnswer? QuestionAnswer,
        bool BudgetAdmitted,
        ChatGoal? Goal);

    /// <summary>A web follow-up waiting to run as its own turn.</summary>
    internal sealed record QueuedFollowUp(string MessageId, string Text, ChatRequestContext Context)
    {
        public IReadOnlyList<WorkspaceFileReference> WorkspaceFiles => Context.Input.ForwardedProperties is { ValueKind: JsonValueKind.Object } forwarded &&
            forwarded.TryGetProperty("workspaceFiles", out var files)
                ? files.Deserialize<WorkspaceFileReference[]>(JsonSerializerOptions.Web) ?? [] : [];
        public string? RecipientAgentId => Context.Input.ForwardedProperties is { ValueKind: JsonValueKind.Object } forwarded &&
            forwarded.TryGetProperty("recipientAgentId", out var recipient) &&
            recipient.ValueKind == JsonValueKind.String ? recipient.GetString() : null;
    }
}
