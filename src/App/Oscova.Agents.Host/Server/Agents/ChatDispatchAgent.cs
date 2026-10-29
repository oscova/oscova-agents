using Oscova.Agents.Infrastructure.Tasks;
using Oscova.Agents.Templating;
using Oscova.Agents.Conversations;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Avatar;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Avatar;
using Oscova.Agents.Core.Speech;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.ToolGovernance;
using Oscova.Agents.Llm;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Hooks;
using Oscova.Agents.Host.Server.Chat;
using Oscova.Agents.Host.Server.Services;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Infrastructure.Hooks;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Metrics;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Middleware;
using Oscova.Agents.Notifications;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;
using Oscova.Agents.Workspace;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Oscova.Agents.Host.Server.Agents;

#pragma warning disable MAAI001 // Goal continuation uses the released experimental loop API.

/// <summary>Supplies authenticated ownership and AG-UI options to detached chat execution.</summary>
internal sealed class ChatDispatchRunOptions : AgentRunOptions
{
    public bool AllowUserQuestions { get; init; }
    public bool ContinueSession { get; init; }
    public bool SessionRewound { get; init; }
    public UserQuestionAnswer? QuestionAnswer { get; init; }
    public ChatGoal? Goal { get; init; }
    public Func<bool>? HasQueuedWork { get; init; }
    public Func<Task>? OnTitleGenerationCompleted { get; init; }

    public required string UserId { get; init; }

    public required ChatOptions ChatOptions { get; init; }

    public required ChatRunProjection Projection { get; init; }
    public required Oscova.Agents.Core.Auth.ExecutionPermissionRegistry.ConfigurationScope ConfigurationScope { get; init; }
}

// Resolves the live ChatThread, builds the inner ChatClientAgent, and yields its streaming
// updates so the AG-UI SSE pipeline can translate them to TEXT_MESSAGE_*/TOOL_CALL_* events.
public sealed class ChatDispatchAgent(
    Oscova.Agents.Secrets.IContentProtector contentProtector,
    UserServiceManager userServiceManager,
    IChatAttachmentService chatAttachmentService,
    ChatAttachmentManifestBuilder attachmentManifestBuilder,
    IAgentRuntimeHookService runtimeHookService,
    RunContextAccessor runContextAccessor,
    IMetricsEventStore metricsEventStore,
    IEgressAuditStore egressAuditStore,
    IToolQuotaUsageStore quotaUsageStore,
    ChatSessionService chatSessionService,
    ChatThreadAgentSessionStore sessionStore,
    IAvatarRuntimeSettingsService avatarRuntimeSettings,
    IAvatarBridgeService avatarBridgeService,
    NotificationHub<ChatThreadChanged> chatThreadNotifications,
    ILogger<ChatDispatchAgent> logger,
    ChatSteeringRegistry steeringRegistry,
    ModelBudgetService budgets,
    IUserPermissionService permissions,
    ThreadRunGuard runGuard) : AIAgent
{
    private const string ToolApprovalToolName = "request_approval";
    private const string ToolApprovalCallIdPrefix = "approve_";
    private const int TitleGenerationMinimumMessageCount = 4;

    private const string ContinuationMarkerKey = "Oscova.Chat.Continuation";

    private readonly ConcurrentDictionary<string, byte> _queuedTitleGenerations = new(StringComparer.Ordinal);

    public override string? Name => "default";

    // Session lifecycle is owned by ChatThreadAgentSessionStore, which resolves the actual
    // owning ChatClientAgent from thread.AgentId. AG-UI never invokes these on the dispatch
    // agent; they exist only because AIAgent declares them abstract.
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ChatDispatchAgent session lifecycle is owned by ChatThreadAgentSessionStore.");

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ChatDispatchAgent session lifecycle is owned by ChatThreadAgentSessionStore.");

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ChatDispatchAgent session lifecycle is owned by ChatThreadAgentSessionStore.");

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ChatDispatchAgent only supports streaming via AG-UI.");

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prep = await PrepareRunAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var ownerSession = session ?? throw new InvalidOperationException("The owning chat session is unavailable.");
        using var todoReader = new TodoProvider();
        var participants = ChatParticipantSessions.Load(ownerSession);
        participants.Join(prep.OwnerDefinition.Id);
        var participant = participants.Join(prep.AgentDefinition.Id);
        if (prep.AgentDefinition.Id != prep.OwnerDefinition.Id)
        {
            session = participant.Session is { } saved
                ? await prep.InnerAgent.DeserializeSessionAsync(saved, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await prep.InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        participants.LastRecipientId = prep.AgentDefinition.Id;
        participants.Save(ownerSession);
        var runStopwatch = Stopwatch.StartNew();

        // This scope carries no userId, so NLog routes it to the shared system log. An incognito
        // thread id must not land there; the remaining properties carry no conversation identity.
        var diagnosticsProperties = new Dictionary<string, object?>
        {
            ["operationId"] = prep.RunId,
            ["stage"] = "chat.run",
            ["agentId"] = prep.AgentDefinition.Id
        };

        if (!prep.Thread.IsIncognito)
        {
            diagnosticsProperties["threadId"] = prep.Thread.Id;
        }

        using var diagnosticsScope = logger.BeginScope(diagnosticsProperties);

        logger.LogInformation(
            "Chat lifecycle event {eventName} started with status {status}.",
            "ChatRunStarted",
            "started");

        var regeneration = ResolveLatestRegeneration(prep, session, ((ChatDispatchRunOptions)options!).SessionRewound);
        var runMessageStartIndex = 0;
        var userMessage = prep.ContinueSession || prep.Goal is not null ? null : regeneration?.UserMessage ?? new ChatDisplayMessage
        {
            MessageId = prep.Projection.UserMessageId,
            Channel = HookExecutionKinds.WebChat,
            From = MessageOwner.User,
            Text = prep.UserText,
            RecipientAgentId = prep.AgentDefinition.Id,
            Attachments = prep.Attachments,
            WorkspaceFiles = prep.WorkspaceFiles
        };
        lock (prep.Thread.DisplayMessages)
        {
            if (regeneration is { } replacement)
            {
                while (prep.Thread.DisplayMessages.Count > replacement.DisplayStartIndex)
                {
                    prep.Thread.DisplayMessages.RemoveAt(prep.Thread.DisplayMessages.Count - 1);
                }
            }
            else if (userMessage is not null && prep.Thread.DisplayMessages.Any(message =>
                         string.Equals(message.MessageId, userMessage.MessageId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Message id '{userMessage.MessageId}' already exists in chat thread '{prep.Thread.Id}'.");
            }

            runMessageStartIndex = prep.Thread.DisplayMessages.Count;
            if (userMessage is not null)
            {
                prep.Thread.DisplayMessages.Add(userMessage);
            }
        }

        if (string.IsNullOrWhiteSpace(prep.Thread.Title))
        {
            prep.Thread.Title = ChatDisplayMessageFormatter.BuildPlaceholderTitle(prep.UserText, prep.Attachments, prep.WorkspaceFiles);
        }

        prep.Thread.AgentId = prep.OwnerDefinition.Id;
        prep.Thread.AgentName = prep.OwnerDefinition.Name;
        prep.Thread.IsUnread = false;
        IReadOnlyList<AllowedDirectory> standardRunDirectories = ChatRunAccess.BuildStandardRunDirectories(
            chatAttachmentService,
            prep.UserScope.UserId,
            prep.Thread);
        standardRunDirectories = standardRunDirectories.Concat(prep.UserScope.ProjectAccess.ResolveRunDirectories(
            prep.Thread.ProjectId, prep.ExecutionProviderId)).ToArray();
        await prep.UserScope.ChatThreadRepository.SaveThreadAsync(prep.UserScope.UserId, prep.Thread, ct: cancellationToken).ConfigureAwait(false);
        // Regeneration replaces existing messages, so append-only recall must rebuild this conversation.
        if (regeneration is not null)
        {
            await prep.UserScope.ChatMemoryService.DeleteThreadAsync(prep.UserScope.UserId, prep.Thread.Id,
                CancellationToken.None).ConfigureAwait(false);
        }

        await NotifyThreadChangedAsync(prep.UserScope.UserId).ConfigureAwait(false);
        prep.Projection.Publish(
            userMessage is null ? [] : [userMessage],
            ChatRunProjectionPhase.Starting,
            ChatRunChangeKind.Structural);

        if (regeneration is { } accepted)
        {
            accepted.History.RemoveRange(
                accepted.HistoryStartIndex,
                accepted.History.Count - accepted.HistoryStartIndex);
        }

        // Stream updates, invocation starts, and interaction prompts share one reader.
        // Prompts remain visible while a tool is waiting for the user's response.
        var channel = Channel.CreateUnbounded<(AgentResponseUpdate? Update, FunctionCallContent? ToolCall, DateTimeOffset? CompletedAt, ChatDisplayMessage? Steered, IReadOnlyList<ChatTodoItem>? Todos, bool IncludeChatHistory, TaskCompletionSource? Committed)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        using var displayDeliveryStopped = new CancellationTokenSource();
        var egressLedger = new EgressLedger();
        var usageLedger = new UsageLedger();
        var toolInvocationLedger = new ToolInvocationLedger();
        var fallbackSignal = new FallbackActivationSignal();
        var historySummarizedSignal = new HistorySummarizedSignal();
        ChatDisplayMessage? historyNoticeMessage = null;
        var segmentParser = new ChatSegmentParser();
        var speakerId = prep.AgentDefinition.Id;
        var speakerName = prep.AgentDefinition.Name;
        var speakerIncludesChatHistory = true;
        ChatDisplayMessage? botMessage = null;
        var assistantProgressPendingPersistence = false;
        var runCompleted = false;
        var runCancelled = false;
        var interruptedUpdates = new List<ChatResponseUpdate>();
        var inputText = prep.RuntimeUserText;
        if (prep.ContinueSession && prep.Goal is null)
        {
            inputText = await prep.UserScope.CrewService.ReadPromptInstructionAsync(
                prep.UserScope.UserId, PromptFileNames.ChatContinuationPrompt, cancellationToken).ConfigureAwait(false)
                ?? SeedContentProvider.GetInstruction(PromptFileNames.ChatContinuationPrompt);
        }

        var inputMessage = new ChatMessage(ChatRole.User, inputText);
        if (prep.ContinueSession || prep.Goal is not null)
        {
            // Regeneration distinguishes internal control turns from the visible user's request.
            inputMessage.AdditionalProperties = new() { [ContinuationMarkerKey] = true };
        }

        var historyBeforeRun = session is not null && session.TryGetInMemoryChatHistory(out var history)
            ? history.ToList()
            : [];
        var sharedMessages = ChatParticipantSessions.GetSharedMessages(prep.Thread, participant, userMessage?.MessageId);
        if (sharedMessages.Count > 0)
        {
            historyBeforeRun.AddRange(sharedMessages);
            session!.SetInMemoryChatHistory(historyBeforeRun.ToList());
        }

        if (userMessage is not null) { participant.SeenMessageIds.Add(userMessage.MessageId); }

        var emittedElicitations = new Dictionary<Guid, (string CallId, DateTimeOffset PromptCreatedAt)>();
        var emittedApprovals = new Dictionary<string, (string CallId, DateTimeOffset PromptCreatedAt)>(StringComparer.Ordinal);
        var emittedLock = new Lock();

        void RefreshBotMessage()
        {
            if (botMessage is null)
            {
                return;
            }

            // Artifact identity must include the producer before live projection and persistence.
            botMessage.AgentId = speakerId;
            botMessage.AgentName = speakerName;
            // Goal replies continue a harness interaction rather than a regeneratable human request.
            botMessage.IsInteractionResponse = prep.Goal is not null;
            // Only the first reply after automatic compaction carries the notice.
            if (historySummarizedSignal.Summarized) { historyNoticeMessage ??= botMessage; }

            botMessage.HistorySummarized = ReferenceEquals(historyNoticeMessage, botMessage);
            botMessage.Segments = [.. segmentParser.Segments];
            botMessage.Text = string.Concat(
                segmentParser.Segments
                    .Where(segment => segment.Type == ChatSegmentType.Text)
                    .Select(segment => segment.Text));

            var usage = usageLedger.Snapshot();
            var totals = MetricsEventBuilder.SumUsage(usage);
            if (usage.Count > 0)
            {
                botMessage.InputTokens = totals.Input;
                botMessage.OutputTokens = totals.Output;
                botMessage.TotalTokens = totals.Total;
            }
        }

        void PublishProjection(ChatRunProjectionPhase phase, ChatRunChangeKind kind, IReadOnlyList<ChatTodoItem>? todos = null)
        {
            lock (prep.Thread.DisplayMessages)
            {
                RefreshBotMessage();
                var runMessages = prep.Thread.DisplayMessages
                    .Skip(runMessageStartIndex)
                    .ToList();
                if (botMessage is { Segments.Count: > 0 } &&
                    !runMessages.Contains(botMessage))
                {
                    runMessages.Add(botMessage);
                }

                prep.Projection.Publish(runMessages, phase, kind, todos);
            }
        }

        ChatDisplayMessage? FindInteractionMessage(AgentResponseUpdate update)
        {
            var callId = update.Contents?
                .OfType<FunctionCallContent>()
                .Select(content => content.CallId)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(callId))
            {
                return null;
            }

            lock (prep.Thread.DisplayMessages)
            {
                if (callId.StartsWith("elicit_", StringComparison.Ordinal) &&
                    Guid.TryParseExact(callId["elicit_".Length..], "N", out var requestId))
                {
                    return prep.Thread.DisplayMessages.FirstOrDefault(message =>
                        message.Elicitation?.RequestId == requestId);
                }

                if (callId.StartsWith(ToolApprovalCallIdPrefix, StringComparison.Ordinal))
                {
                    var approvalRequestId = callId[ToolApprovalCallIdPrefix.Length..];
                    return prep.Thread.DisplayMessages.FirstOrDefault(message =>
                        string.Equals(message.ToolApproval?.RequestId, approvalRequestId, StringComparison.Ordinal));
                }

                return null;
            }
        }

        void CommitBotMessageIfAny(ChatDisplayMessage? insertBefore = null)
        {
            if (botMessage is null)
            {
                return;
            }

            segmentParser.Complete();
            RefreshBotMessage();
            lock (prep.Thread.DisplayMessages)
            {
                var insertionIndex = insertBefore is null
                    ? -1
                    : prep.Thread.DisplayMessages.IndexOf(insertBefore);
                if (insertionIndex >= 0)
                {
                    prep.Thread.DisplayMessages.Insert(insertionIndex, botMessage);
                }
                else
                {
                    prep.Thread.DisplayMessages.Add(botMessage);
                }
            }

            assistantProgressPendingPersistence = true;
            botMessage = null;
            segmentParser.Reset();
        }

        async Task SaveThreadForAssistantProgressAsync()
        {
            if (!assistantProgressPendingPersistence)
            {
                return;
            }

            await prep.UserScope.ChatThreadRepository.SaveThreadAsync(
                prep.UserScope.UserId,
                prep.Thread,
                ct: CancellationToken.None).ConfigureAwait(false);
            assistantProgressPendingPersistence = false;
            await NotifyThreadChangedAsync(prep.UserScope.UserId).ConfigureAwait(false);
        }

        // A question posted while workers run becomes answerable at this boundary, not at run completion.
        async Task SaveQuestionBoundaryAsync()
        {
            var posted = false;
            lock (prep.Thread.DisplayMessages)
            {
                for (var index = runMessageStartIndex; index < prep.Thread.DisplayMessages.Count; index++)
                {
                    var message = prep.Thread.DisplayMessages[index];
                    var finalized = UserQuestions.Finalize(message);
                    if (!ReferenceEquals(finalized, message))
                    {
                        prep.Thread.DisplayMessages[index] = finalized;
                        posted = true;
                    }
                }
            }

            if (posted)
            {
                // The model session is saved first, so a saved card always has a usable session boundary.
                await sessionStore.SaveForUserAsync(prep.UserScope.UserId, prep.Thread.Id, ownerSession,
                    prep.RequestedModelProviderId, CancellationToken.None).ConfigureAwait(false);
                assistantProgressPendingPersistence = true;
            }

            await SaveThreadForAssistantProgressAsync().ConfigureAwait(false);
        }

        string? LatestQuestionNote()
        {
            lock (prep.Thread.DisplayMessages)
            {
                var card = prep.Thread.DisplayMessages.Skip(runMessageStartIndex)
                    .SelectMany(message => message.Segments.Select(segment =>
                        UserQuestions.TryRead(message, segment, out var read) ? read : null))
                    .LastOrDefault(read => read is not null);
                return card is null ? null : UserQuestions.GetState(prep.Thread, card) switch
                {
                    UserQuestions.CardState.Pending => UserQuestions.PendingReminder,
                    UserQuestions.CardState.Dismissed => UserQuestions.DismissedNotice,
                    _ => null
                };
            }
        }

        void OnElicitationStateChanged()
        {
            lock (emittedLock)
            {
                ChatDisplayMessage[] messages;
                lock (prep.Thread.DisplayMessages)
                {
                    messages = [.. prep.Thread.DisplayMessages];
                }

                var current = messages
                    .Where(message => message.Elicitation is not null)
                    .Select(message => message.Elicitation!.RequestId)
                    .ToHashSet();

                foreach (var newId in current.Where(id => !emittedElicitations.ContainsKey(id)).ToList())
                {
                    var promptMessage = messages.First(message => message.Elicitation?.RequestId == newId);
                    var elicitation = promptMessage.Elicitation!;
                    var callId = "elicit_" + newId.ToString("N");
                    emittedElicitations[newId] = (callId, promptMessage.CreatedAt);
                    var update = BuildElicitationCallUpdate(elicitation, callId);
                    update.AgentId = runContextAccessor.Current?.CurrentAgentId ?? prep.AgentDefinition.Id;
                    channel.Writer.TryWrite((update, null, null, null, null, true, null));
                }

                foreach (var doneId in emittedElicitations.Keys.Where(id => !current.Contains(id)).ToList())
                {
                    var (callId, promptCreatedAt) = emittedElicitations[doneId];
                    emittedElicitations.Remove(doneId);
                    var userResponseMessage = messages.LastOrDefault(message =>
                        message.From == MessageOwner.User &&
                        message.CreatedAt >= promptCreatedAt);
                    var update = BuildElicitationResultUpdate(callId, userResponseMessage);
                    update.AgentId = runContextAccessor.Current?.CurrentAgentId ?? prep.AgentDefinition.Id;
                    channel.Writer.TryWrite((update, null, null, null, null, true, null));
                }
            }
        }

        void OnToolApprovalStateChanged()
        {
            lock (emittedLock)
            {
                // Mirror elicitation: derive bridge events from the approval card's visible lifecycle.
                ChatDisplayMessage[] messages;
                lock (prep.Thread.DisplayMessages)
                {
                    messages = [.. prep.Thread.DisplayMessages];
                }

                var current = messages
                    .Where(message => message.ToolApproval is { Outcome: null })
                    .Select(message => message.ToolApproval!.RequestId)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var newId in current.Where(id => !emittedApprovals.ContainsKey(id)).ToList())
                {
                    var promptMessage = messages.First(message =>
                        string.Equals(message.ToolApproval?.RequestId, newId, StringComparison.Ordinal));
                    var approval = promptMessage.ToolApproval!;
                    var callId = ToolApprovalCallIdPrefix + newId;
                    emittedApprovals[newId] = (callId, promptMessage.CreatedAt);
                    var update = BuildToolApprovalCallUpdate(approval, callId);
                    update.AgentId = approval.AgentId;
                    channel.Writer.TryWrite((update, null, null, null, null, true, null));
                }

                foreach (var doneId in emittedApprovals.Keys.Where(id => !current.Contains(id)).ToList())
                {
                    var (callId, promptCreatedAt) = emittedApprovals[doneId];
                    emittedApprovals.Remove(doneId);
                    var userResponseMessage = messages.LastOrDefault(message =>
                        message.From == MessageOwner.User &&
                        message.CreatedAt >= promptCreatedAt);
                    var card = messages.FirstOrDefault(message => message.ToolApproval?.RequestId == doneId);
                    var update = BuildToolApprovalResultUpdate(callId, userResponseMessage);
                    update.AgentId = card?.ToolApproval?.AgentId;
                    channel.Writer.TryWrite((update, null, null, null, null, true, null));
                    if (card is not null)
                    {
                        // A card saved while pending, such as a worker's, must also save its decision.
                        channel.Writer.TryWrite((null, null, null, card, null, true,
                            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));
                    }
                }
            }
        }

        var elicitationContext = new ElicitationContext
        {
            UserId = prep.UserScope.UserId,
            Channel = ElicitationChannel.WebUI,
            ChannelMetadata = new WebElicitationMetadata
            {
                ChatThread = prep.Thread,
                OnStateChanged = OnElicitationStateChanged
            }
        };
        var approvalContext = new ToolApprovalContext
        {
            UserId = prep.UserScope.UserId,
            Channel = ToolApprovalChannel.WebUI,
            RunMode = prep.ApprovalRunMode,
            ChannelMetadata = new WebToolApprovalMetadata
            {
                ChatThread = prep.Thread,
                OnStateChanged = OnToolApprovalStateChanged
            }
        };
        var activeParticipants = new HashSet<string>(StringComparer.Ordinal) { prep.AgentDefinition.Id };
        ChatConsultation? consultation = null;
        consultation = new ChatConsultation
        {
            InvokeAsync = async (agent, query, includeChatHistory, invoke, ct) =>
            {
                var guest = participants.Find(agent.Id)
                    ?? throw new InvalidOperationException("This agent has not joined the chat. Ask the user to invite it, or use destination delegated.");
                lock (activeParticipants)
                {
                    if (!activeParticipants.Add(agent.Id))
                    {
                        throw new InvalidOperationException("This participant is already responding. Wait for that turn to finish or use delegated work.");
                    }
                }

                var previousStreamAgent = consultation!.StreamingAgentId;
                var previousUpdate = consultation.OnUpdate;
                AgentSession? childSession = null;
                try
                {
                    var definition = prep.UserScope.AppService.ResolveAgentDefinition(agent.Id);
                    var providerId = AgentSelectionResolver.ResolveModelProviderId(prep.OwnerDefinition, definition,
                        prep.ChatModelProviderId, prep.UserScope.ModelProviderSettings.SelectedChatModelProviderId?.ToString());
                    // Shared calls use the participant's configured context without overwriting owner recovery capture.
                    agent = await CreateObservedAgentAsync(prep.UserScope, definition, providerId,
                        new AgentRunObserverAccessor(), prep.ToolObserverAccessor).ConfigureAwait(false);
                    childSession = !includeChatHistory ? await agent.CreateSessionAsync(ct).ConfigureAwait(false)
                        : agent.Id == prep.OwnerDefinition.Id ? ownerSession : guest.Session is { } childState
                        ? await agent.DeserializeSessionAsync(childState, cancellationToken: ct).ConfigureAwait(false)
                        : await agent.CreateSessionAsync(ct).ConfigureAwait(false);
                    var childHistory = childSession.TryGetInMemoryChatHistory(out var prior) ? prior.ToList() : [];
                    if (includeChatHistory)
                    {
                        childHistory.AddRange(ChatParticipantSessions.GetSharedMessages(prep.Thread, guest));
                    }

                    childSession.SetInMemoryChatHistory(childHistory.ToList());
                    var childInput = new ChatMessage(ChatRole.User, $"[{runContextAccessor.Current?.CurrentAgentName}]\n{query}");
                    var childUpdates = new List<ChatResponseUpdate>();
                    consultation.StreamingAgentId = agent.Id;
                    consultation.OnUpdate = update =>
                    {
                        var forwarded = WithoutApprovalRequests(update);
                        if (forwarded is null) { return; }

                        forwarded.AgentId = agent.Id;
                        childUpdates.Add(forwarded.AsChatResponseUpdate().Clone());
                        channel.Writer.TryWrite((forwarded, null, null, null, null, includeChatHistory, null));
                    };
                    try
                    {
                        var response = await invoke(agent, [childInput], childSession, ct).ConfigureAwait(false);
                        // The next consultation must see this completed answer before preparing its model input.
                        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, displayDeliveryStopped.Token);
                        await channel.Writer.WriteAsync((null, null, null, null, null, includeChatHistory, committed), deliveryCancellation.Token).ConfigureAwait(false);
                        await committed.Task.WaitAsync(deliveryCancellation.Token).ConfigureAwait(false);
                        return response;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        InterruptedHistory.Restore(childSession, InterruptedHistory.Build(childHistory, [childInput], childUpdates));
                        throw;
                    }
                }
                finally
                {
                    try
                    {
                        // Question-only calls leave the participant's saved session and transcript cursor intact.
                        if (includeChatHistory && childSession is not null && agent.Id != prep.OwnerDefinition.Id)
                        {
                            guest.Session = await agent.SerializeSessionAsync(childSession, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        }

                        participants.Save(ownerSession);
                    }
                    finally
                    {
                        consultation.StreamingAgentId = previousStreamAgent;
                        consultation.OnUpdate = previousUpdate;
                        lock (activeParticipants) { activeParticipants.Remove(agent.Id); }
                    }
                }
            }
        };
        var runContext = new RunContext
        {
            UserId = prep.UserScope.UserId,
            ExecutionKind = RunExecutionKind.WebChat,
            ExecutionId = prep.Thread.Id,
            Elicitation = elicitationContext,
            ProjectId = prep.Thread.ProjectId,
            IsIncognito = prep.Thread.IsIncognito,
            ToolApproval = approvalContext,
            Consultation = consultation,
            AirGapped = prep.UserScope.PrivacySettings.IsLocalOnly || prep.OwnerDefinition.IsLocalOnly || prep.AgentDefinition.IsLocalOnly,
            ExecutionProviderId = prep.ExecutionProviderId,
            ConfiguredExecutionProviderId = prep.ConfiguredExecutionProviderId,
            PermissionPreset = prep.PermissionPreset,
            ExecutionDevice = prep.ExecutionDevice,
            StandardRunDirectories = standardRunDirectories,
            AllowedDirectories = prep.UserScope.AppService.CrewService.ResolveAllowedDirectories(
                prep.UserScope.UserId,
                prep.AgentDefinition,
                standardRunDirectories),
            ToolApprovalRunMode = prep.ApprovalRunMode,
            EgressLedger = egressLedger,
            UsageLedger = usageLedger,
            ToolInvocationLedger = toolInvocationLedger,
            FallbackSignal = fallbackSignal,
            HistorySummarizedSignal = historySummarizedSignal,
            CurrentAgentName = prep.AgentDefinition.Name,
            CurrentAgentId = prep.AgentDefinition.Id,
            QuestionAgentId = prep.AllowUserQuestions ? prep.AgentDefinition.Id : null
        };

        async Task FinalizeTelemetryAsync()
        {
            var metricsUsage = usageLedger.Snapshot();
            var metricsEgress = egressLedger.Snapshot();
            var metricsTools = toolInvocationLedger.Snapshot();
            var incognito = prep.Thread.IsIncognito;

            {
                var metricsEvent = MetricsEventBuilder.BuildEvent(
                    runContext,
                    "web_chat",
                    metricsUsage,
                    metricsTools,
                    fallbackSignal.Activated) with
                {
                    RunId = prep.RunId
                };

                try
                {
                    await metricsEventStore.AppendAsync(prep.UserScope.UserId,
                        incognito ? MetricsEventBuilder.Anonymous(metricsUsage, MetricsEventBuilder.IncognitoBucket) : metricsEvent, CancellationToken.None).ConfigureAwait(false);
                    await budgets.NotifyCompletedAsync(prep.UserScope.UserId, metricsEvent).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to append metrics event for chat run {RunId}.", prep.RunId);
                }
            }

            try
            {
                // The egress ledger still records the run, de-identified, so incognito cannot hide cloud traffic.
                var auditLines = EgressAuditEventBuilder.BuildRunLines(
                    runContext,
                    metricsEgress,
                    metricsTools,
                    EgressAuditKinds.SourceChat,
                    incognito ? null : prep.Thread.Id,
                    incognito ? prep.RunId : null,
                    null,
                    DateTimeOffset.UtcNow);
                await egressAuditStore.AppendRangeAsync(prep.UserScope.UserId, auditLines, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to append egress audit lines for chat run {RunId}.", prep.RunId);
            }
        }

        // Keep configuration dependencies alive while an invocation waits for approval or continues.
        var configuration = ((ChatDispatchRunOptions)options!).ConfigurationScope;
        var delegatedWork = new DelegatedWorkState(configuration.Token);
        var waitingForSubagents = false;
        runContext = runContext.With(configurationScope: configuration, delegatedWork: delegatedWork);
        var runContextScope = runContextAccessor.SetContext(runContext);
        await using (runContextScope.ConfigureAwait(false))
        {

            var runCt = configuration.Token;

            var innerSession = session;
            GoalRunLifecycle? goalLifecycle = null;
            var steering = steeringRegistry.Register(
                prep.UserScope.UserId,
                prep.Thread.Id,
                prep.AgentDefinition.Id,
                prep.InnerAgent,
                innerSession!,
                prep.RunId,
                runContext,
                async (message, ct) =>
                {
                    if (participants.Participants.Count > 1)
                    {
                        await sessionStore.CaptureSteeringTurnAsync(prep.UserScope.UserId, prep.Thread.Id,
                            prep.RequestedModelProviderId, ownerSession, prep.InnerAgent, innerSession!,
                            goalLifecycle is null ? InterruptedHistory.Build(historyBeforeRun, [inputMessage], interruptedUpdates)
                                : InterruptedHistory.Build(goalLifecycle.HistoryBeforeCycle, goalLifecycle.CycleInputs, goalLifecycle.CycleUpdates),
                            message.MessageId, ct).ConfigureAwait(false);
                    }
                    // Applied steers keep their position between native assistant and tool messages.
                    interruptedUpdates.Add(new ChatResponseUpdate(ChatRole.User, ChatDisplayMessageFormatter.ComposeFiles(message.Text, message.WorkspaceFiles))
                    {
                        MessageId = message.MessageId
                    });
                    channel.Writer.TryWrite((null, null, null, message, null, true, null));
                },
                async (message, ct) =>
                {
                    // The consumer owns the live transcript, so it applies and saves the replacement.
                    var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!channel.Writer.TryWrite((null, null, null, message, null, true, committed))) { return false; }

                    using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, displayDeliveryStopped.Token);
                    await committed.Task.WaitAsync(deliveryCancellation.Token).ConfigureAwait(false);
                    return true;
                });
            if (prep.Goal is { } goal)
            {
                var judgeInstructions = await prep.UserScope.CrewService.ReadPromptInstructionAsync(
                    prep.UserScope.UserId, PromptFileNames.GoalJudgePrompt, runCt).ConfigureAwait(false)
                    ?? AIJudgeLoopEvaluator.DefaultInstructions;
                var continuationTemplate = await prep.UserScope.CrewService.ReadPromptInstructionAsync(
                    prep.UserScope.UserId, PromptFileNames.GoalContinuationPrompt, runCt).ConfigureAwait(false)
                    ?? AIJudgeLoopEvaluator.DefaultFeedbackMessageTemplate;
                var judgeClient = new ConfigureOptionsChatClient(prep.ChatClient, judgeOptions =>
                {
                    // Retain the evaluator's response schema while using the worker's selected model options.
                    var workerOptions = prep.RunOptions.ChatOptions?.Clone();
                    judgeOptions.ModelId = workerOptions?.ModelId;
                    judgeOptions.Temperature = workerOptions?.Temperature;
                    judgeOptions.TopP = workerOptions?.TopP;
                    judgeOptions.TopK = workerOptions?.TopK;
                    judgeOptions.FrequencyPenalty = workerOptions?.FrequencyPenalty;
                    judgeOptions.PresencePenalty = workerOptions?.PresencePenalty;
                    judgeOptions.Seed = workerOptions?.Seed;
                    judgeOptions.MaxOutputTokens = workerOptions?.MaxOutputTokens;
                    judgeOptions.StopSequences = workerOptions?.StopSequences;
                    judgeOptions.Reasoning = workerOptions?.Reasoning;
                    judgeOptions.AdditionalProperties = workerOptions?.AdditionalProperties;
                    judgeOptions.Tools = null;
                });
                var judge = new AIJudgeLoopEvaluator(judgeClient, new AIJudgeLoopEvaluatorOptions
                {
                    Instructions = judgeInstructions, FeedbackMessageTemplate = continuationTemplate
                });
                goalLifecycle = new GoalRunLifecycle(prep.UserScope.ConversationGoals, prep.UserScope.UserId,
                    prep.Thread, goal, prep.RunId, prep.InnerAgent, judge, budgets, prep.EffectiveProviderId,
                    usageLedger, steering, ((ChatDispatchRunOptions)options!).HasQueuedWork ?? (() => false),
                    () => delegatedWork.HasUnsettledWork(innerSession!), logger);
            }

            prep.ObserverAccessor.Observer = new ChatRunSessionObserver(prep.Thread, steering,
                goalLifecycle is null ? null : goalLifecycle.CaptureRequest);
            // Serialize invocation notifications with stream updates before publishing display state.
            prep.ToolObserverAccessor.Observer = new ToolExecutionObserver((call, completedAt) => channel.Writer.TryWrite((null, call, completedAt, null, null, true, null)));
            var producerTask = Task.Run(async () =>
            {
                Exception? error = null;
                try
                {
                    // Resume expresses the user's action without adding a visible user bubble.
                    IEnumerable<ChatMessage> nextMessages =
                        [inputMessage];
                    while (true)
                    {
                        _ = prep.UserScope.ProjectAccess.ResolveRunDirectories(prep.Thread.ProjectId, prep.ExecutionProviderId);
                        var pendingApprovals = new List<ToolApprovalRequestContent>();

                        var updates = goalLifecycle is null
                            ? prep.InnerAgent.RunStreamingAsync(nextMessages, innerSession, prep.RunOptions, runCt)
                            : goalLifecycle.RunStreamingAsync(nextMessages, innerSession!, prep.RunOptions, runCt);
                        await foreach (var update in updates
                                           .ConfigureAwait(false))
                        {
                            var approvalRequests = ExtractApprovalRequests(update);
                            var forwarded = approvalRequests.Count == 0 ? update : WithoutApprovalRequests(update);
                            if (forwarded is not null)
                            {
                                // Capture native output before display identifiers or interaction cards alter it.
                                interruptedUpdates.Add(forwarded.AsChatResponseUpdate().Clone());
                                forwarded.AgentId = prep.AgentDefinition.Id;
                                // Freeze working-list values before advancing the native tool stream.
                                IReadOnlyList<ChatTodoItem>? todos = prep.OwnerDefinition.IsTodoEnabled &&
                                    forwarded.Contents?.Any(content => content is FunctionResultContent) == true
                                    ? [.. (await todoReader.GetAllTodosAsync(ownerSession).ConfigureAwait(false))
                                        .Select(item => new ChatTodoItem(item.Id, item.Title, item.Description, item.IsComplete))]
                                    : null;
                                channel.Writer.TryWrite((forwarded, null, null, null, todos, true, null));
                            }

                            pendingApprovals.AddRange(approvalRequests);
                        }

                        if (pendingApprovals.Count == 0)
                        {
                            // The consumer saves the boundary and finalizes posted questions before the parent waits or reads results.
                            async Task CommitBoundaryAsync(CancellationToken ct)
                            {
                                var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, displayDeliveryStopped.Token);
                                await channel.Writer.WriteAsync((null, null, null, null, null, true, committed), deliveryCancellation.Token).ConfigureAwait(false);
                                await committed.Task.WaitAsync(deliveryCancellation.Token).ConfigureAwait(false);
                            }

                            // Owned workers keep the run open. Their results and new input wake the parent once each.
                            var settled = await delegatedWork.SettleAsync(prep.InnerAgent, innerSession!, async ct =>
                            {
                                waitingForSubagents = true;
                                await CommitBoundaryAsync(ct).ConfigureAwait(false);
                            }, runCt).ConfigureAwait(false);
                            var waited = waitingForSubagents;
                            waitingForSubagents = false;
                            if (settled is not null)
                            {
                                if (!waited) { await CommitBoundaryAsync(runCt).ConfigureAwait(false); }
                                // A worker result alone would read as a fresh turn and repeat an open or dismissed question.
                                nextMessages = settled.Count > 0 && LatestQuestionNote() is { } note
                                    ? [.. settled, new ChatMessage(ChatRole.User, note)
                                        .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider)]
                                    : settled;
                                // Delivered results keep their source in the history a Stop rebuilds.
                                foreach (var message in nextMessages)
                                {
                                    interruptedUpdates.Add(new ChatResponseUpdate(message.Role, message.Contents)
                                    {
                                        MessageId = Guid.NewGuid().ToString("N"),
                                        AdditionalProperties = message.AdditionalProperties
                                    });
                                }
                                // A blocked or finished goal still lets the admitted cycle read results and answers.
                                if (goalLifecycle is not null) { goalLifecycle.SettlingSteer = true; }

                                continue;
                            }

                            // A steer that arrived after the last model call still gets its own answer.
                            // Input stays open for a waiting steer's call, which may start workers, unless the goal refused that call.
                            var hasPendingSteer = goalLifecycle?.SettlingSteer == true
                                ? await steering.CloseAsync(runCt).ConfigureAwait(false)
                                : await steering.CloseWhenIdleAsync(runCt).ConfigureAwait(false);
                            if (!hasPendingSteer && goalLifecycle?.NeedsSteerSettlement != true)
                            {
                                break;
                            }

                            nextMessages = [];
                            if (goalLifecycle is not null) { goalLifecycle.SettlingSteer = true; }

                            continue;
                        }

                        var responseContents = new List<AIContent>(pendingApprovals.Count);
                        if (goalLifecycle is not null) { goalLifecycle.ApprovalResume = true; }

                        foreach (var request in pendingApprovals)
                        {
                            var submission = await prep.UserScope.ToolApprovalRouter
                                .RouteAsync(request, approvalContext, runCt)
                                .ConfigureAwait(false);
                            responseContents.Add(submission.Response);
                        }

                        nextMessages = [new ChatMessage(ChatRole.User, responseContents)];
                    }
                }
                catch (HookDeniedException ex)
                {
                    error = ex;
                }
                catch (OperationCanceledException) when (runCt.IsCancellationRequested)
                {
                    // A user stop or host shutdown is normal producer completion, not a failed channel.
                    runCancelled = true;
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    // Stop cascades to workers, and they drain before the run releases its guard.
                    // Closing steering first refuses answers during the drain instead of accepting and dropping them.
                    await steering.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    await delegatedWork.DisposeAsync().ConfigureAwait(false);
                    steering.Dispose();
                    prep.ObserverAccessor.Observer = null;
                    prep.ToolObserverAccessor.Observer = null;
                    channel.Writer.TryComplete(error);
                }
            }, CancellationToken.None);

            try
            {
                await foreach (var (update, toolCall, completedAt, steered, todos, includeChatHistory, committed) in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (steered is not null && committed is not null)
                    {
                        // A replacement, such as a dismissed question, updates its saved message in place.
                        lock (prep.Thread.DisplayMessages)
                        {
                            var displayMessages = prep.Thread.DisplayMessages;
                            for (var index = 0; index < displayMessages.Count; index++)
                            {
                                if (displayMessages[index].MessageId == steered.MessageId) { displayMessages[index] = steered; }
                            }
                        }
                        // A thread save only appends new rows, so the saved row is rewritten in place.
                        await SaveThreadForAssistantProgressAsync().ConfigureAwait(false);
                        await prep.UserScope.ChatThreadRepository.TryReplaceDisplayMessageAsync(
                            prep.UserScope.UserId, prep.Thread.Id, steered, CancellationToken.None).ConfigureAwait(false);
                        await NotifyThreadChangedAsync(prep.UserScope.UserId).ConfigureAwait(false);
                        PublishProjection(waitingForSubagents ? ChatRunProjectionPhase.AwaitingSubagents : ChatRunProjectionPhase.Streaming,
                            ChatRunChangeKind.Structural);
                        committed.SetResult();
                        continue;
                    }

                    if (committed is not null)
                    {
                        CommitBotMessageIfAny();
                        var waiting = waitingForSubagents;
                        await SaveQuestionBoundaryAsync().ConfigureAwait(false);
                        PublishProjection(waiting ? ChatRunProjectionPhase.AwaitingSubagents : ChatRunProjectionPhase.Streaming,
                            ChatRunChangeKind.Structural);
                        committed.SetResult();
                        continue;
                    }

                    if (steered is not null)
                    {
                        // Text streamed before the steer stays one message. Text after it starts the next.
                        CommitBotMessageIfAny();
                        lock (prep.Thread.DisplayMessages)
                        {
                            prep.Thread.DisplayMessages.Add(steered);
                        }

                        assistantProgressPendingPersistence = true;
                        PublishProjection(ChatRunProjectionPhase.Streaming, ChatRunChangeKind.Structural);
                        continue;
                    }

                    if (toolCall is not null)
                    {
                        lock (prep.Thread.DisplayMessages)
                        {
                            // Approval can partition the call into an already committed message.
                            var segment = segmentParser.Segments
                                .Concat(prep.Thread.DisplayMessages.SelectMany(message => message.Segments))
                                .LastOrDefault(segment => segment.FunctionCall?.CallId == toolCall.CallId);
                            if (segment is not null)
                            {
                                // Argument streaming can finish before invocation. Use the actual tool completion time.
                                segment.ProcessedAt = completedAt;
                                if (completedAt is null && segment.ExecutionState == ToolExecutionState.Pending)
                                {
                                    segment.ExecutionState = ToolExecutionState.Executing;
                                }
                            }
                        }

                        PublishProjection(ChatRunProjectionPhase.Streaming, ChatRunChangeKind.Structural);
                        continue;
                    }

                    if (update is null) { continue; }

                    var nextSpeaker = update.AgentId ?? prep.AgentDefinition.Id;
                    // Different session histories need separate delivery IDs, even for the same speaker.
                    if (nextSpeaker != speakerId || includeChatHistory != speakerIncludesChatHistory)
                    {
                        CommitBotMessageIfAny();
                        speakerId = nextSpeaker;
                        speakerName = prep.UserScope.AppService.ResolveAgentDefinition(speakerId).Name;
                        speakerIncludesChatHistory = includeChatHistory;
                    }

                    if (IsElicitationCallUpdate(update) || IsToolApprovalCallUpdate(update))
                    {
                        botMessage ??= new ChatDisplayMessage
                        {
                            Channel = HookExecutionKinds.WebChat,
                            From = MessageOwner.Bot,
                            Text = string.Empty,
                            CreatedAt = DateTimeOffset.UtcNow
                        };
                        var promptedUpdate = EnsureAssistantMessageId(update, botMessage);
                        // Interaction cards own these synthetic calls, they are not unfinished native tool activity.
                        var interactionMessage = FindInteractionMessage(update);
                        CommitBotMessageIfAny(interactionMessage);
                        PublishProjection(
                            IsElicitationCallUpdate(update)
                                ? ChatRunProjectionPhase.AwaitingElicitation
                                : ChatRunProjectionPhase.AwaitingToolApproval,
                            ChatRunChangeKind.Structural);
                        yield return promptedUpdate;
                        continue;
                    }

                    if (IsElicitationPseudoUpdate(update) || IsToolApprovalPseudoUpdate(update))
                    {
                        PublishProjection(
                            ChatRunProjectionPhase.Continuing,
                            ChatRunChangeKind.Structural);
                        yield return update;
                        continue;
                    }

                    botMessage ??= new ChatDisplayMessage
                    {
                        Channel = HookExecutionKinds.WebChat,
                        From = MessageOwner.Bot,
                        Text = string.Empty,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    var streamUpdate = EnsureAssistantMessageId(update, botMessage);
                    if (!includeChatHistory)
                    {
                        lock (prep.Thread.DisplayMessages)
                        {
                            participants.Find(speakerId)!.QuestionOnlyMessageIds.Add(botMessage.MessageId);
                        }
                    }

                    segmentParser.Process(streamUpdate);
                    PublishProjection(
                        ChatRunProjectionPhase.Streaming,
                        IsStructuralUpdate(streamUpdate)
                            ? ChatRunChangeKind.Structural
                            : ChatRunChangeKind.Delta,
                        todos);
                    yield return streamUpdate;
                }

                if (runCancelled)
                {
                    yield break;
                }

                CommitBotMessageIfAny();
                PublishProjection(
                    ChatRunProjectionPhase.Streaming,
                    ChatRunChangeKind.Structural);
                await prep.UserScope.ChatThreadRepository.SaveThreadAsync(
                    prep.UserScope.UserId,
                    prep.Thread,
                    ct: CancellationToken.None).ConfigureAwait(false);
                assistantProgressPendingPersistence = false;
                await NotifyThreadChangedAsync(prep.UserScope.UserId).ConfigureAwait(false);
                QueueTitleGenerationIfNeeded(prep, ((ChatDispatchRunOptions)options!).OnTitleGenerationCompleted);
                runCompleted = true;

                logger.LogInformation(
                    "Chat lifecycle event {eventName} finished with status {status} after {durationMs} ms.",
                    "ChatRunCompleted",
                    "success",
                    runStopwatch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                displayDeliveryStopped.Cancel();
                // Keep telemetry and the thread lease alive until the inner producer has actually stopped.
                await producerTask.ConfigureAwait(false);
                await FinalizeTelemetryAsync().ConfigureAwait(false);

                if (!runCompleted)
                {
                    CommitBotMessageIfAny();
                    if (runCancelled || runCt.IsCancellationRequested)
                    {
                        // Include calls committed before an approval or elicitation split the message.
                        lock (prep.Thread.DisplayMessages)
                        {
                            foreach (var segment in prep.Thread.DisplayMessages.Skip(runMessageStartIndex)
                                         .SelectMany(message => message.Segments))
                            {
                                if (segment.Type == ChatSegmentType.ToolCall &&
                                    segment.ExecutionState is ToolExecutionState.Pending or ToolExecutionState.Executing)
                                {
                                    segment.ExecutionState = ToolExecutionState.Cancelled;
                                    segment.State = ChatSegmentState.Completed;
                                    segment.ProcessedAt = DateTimeOffset.UtcNow;
                                    assistantProgressPendingPersistence = true;
                                }
                            }
                        }
                    }

                    PublishProjection(
                        ChatRunProjectionPhase.Streaming,
                        ChatRunChangeKind.Structural);

                    if (session is not null)
                    {
                        if (goalLifecycle is not null)
                        {
                            InterruptedHistory.Restore(session, InterruptedHistory.Build(goalLifecycle.HistoryBeforeCycle,
                                goalLifecycle.CycleInputs, goalLifecycle.CycleUpdates));
                        }
                        else if (runCancelled || runCt.IsCancellationRequested)
                        {
                            InterruptedHistory.Restore(session, InterruptedHistory.Build(
                                historyBeforeRun,
                                [inputMessage],
                                interruptedUpdates));
                        }
                        else
                        {
                            var partialHistory = new List<ChatMessage>(historyBeforeRun);
                            if (!prep.ContinueSession)
                            {
                                partialHistory.Add(new ChatMessage(ChatRole.User, prep.RuntimeUserText));
                            }

                            partialHistory.AddRange(steering.AppliedTexts.Select(text => new ChatMessage(ChatRole.User, text)));
                            InterruptedHistory.Restore(session, partialHistory);
                        }
                    }

                    try
                    {
                        await SaveThreadForAssistantProgressAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Failed to persist partial assistant progress for interrupted chat run {RunId}.",
                            prep.RunId);
                    }
                }

                if (prep.AgentDefinition.Id != prep.OwnerDefinition.Id)
                {
                    participant.Session = await prep.InnerAgent.SerializeSessionAsync(session!, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }

                participants.Save(ownerSession);
            }
        }
    }

    private async Task<RunPreparation> PrepareRunAsync(
        IEnumerable<ChatMessage> messages,
        AgentRunOptions? options,
        CancellationToken ct)
    {
        var runOptions = options as ChatDispatchRunOptions
            ?? throw new InvalidOperationException("Chat dispatch requires server-owned run options.");
        var userId = runOptions.UserId;
        if (!contentProtector.IsAvailable(userId))
        {
            throw new UnauthorizedAccessException("Unlock your Vault Password before continuing.");
        }

        var (threadId, runId, forwardedProps) = ExtractAgUiInput(runOptions.ChatOptions);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("AG-UI request missing required threadId.");
        }

        var effectiveRunId = runId ?? Guid.NewGuid().ToString("N");
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var (modelProviderId, agentId, attachmentIds, approvalRunMode, regenerateAssistantMessageId) =
            ParseForwardedProps(forwardedProps);
        var requestedModelProviderId = modelProviderId;
        string? shellExecutionMode = userScope.ExecutionSettings.ProviderId;

        await userScope.AppService.ReloadAgentDefinitionsAsync().ConfigureAwait(false);

        var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        var isNewThread = thread is null;
        thread ??= new ChatThread { Id = threadId };
        var preset = await userScope.ConversationChoices.GetAsync(userId, thread, ct).ConfigureAwait(false);
        // Saved only by an admitted run, so rejected starts and queued follow-ups agree on the model reopening restores.
        if (!isNewThread && Guid.TryParse(requestedModelProviderId, out var chosenProviderId))
        {
            await userScope.ConversationChoices.SaveModelProviderIdAsync(userId, thread, chosenProviderId, ct).ConfigureAwait(false);
        }

        await ConversationExecutionAccess.RequireAsync(permissions, userId, preset, ct).ConfigureAwait(false);
        var configuredProviderId = shellExecutionMode;
        shellExecutionMode = ConversationExecutionAccess.ResolveProviderId(preset, configuredProviderId);
        approvalRunMode = preset switch
        {
            ChatPermissionPreset.AutoApprove or ChatPermissionPreset.FullAccess => ToolApprovalRunMode.AutoApproveRequired,
            ChatPermissionPreset.AutoReject => ToolApprovalRunMode.AutoRejectRequired,
            _ => ToolApprovalRunMode.UseAgentPolicy
        };
        var project = thread.ProjectId is null ? null
            : await userScope.ProjectAccess.AuthorizeAsync(thread.ProjectId, ct).ConfigureAwait(false);
        var executionDevice = project?.NodeId ?? Environment.MachineName;
        runOptions.Projection.SetPermissionPreset(preset);
        var projectDirectories = userScope.ProjectAccess.ResolveRunDirectories(thread.ProjectId, shellExecutionMode);
        var userText = ExtractUserText(messages);
        if (runOptions.QuestionAnswer is { } answer)
        {
            // Recheck saved state under the admission lease before the ordinary user-message append.
            var card = UserQuestions.Find(thread, answer.MessageId, answer.CallId);
            if (!runOptions.AllowUserQuestions || regenerateAssistantMessageId is not null ||
                card is null || UserQuestions.GetState(thread, card) != UserQuestions.CardState.Pending ||
                card.SavedState!.AnswerMessageId != runOptions.Projection.UserMessageId ||
                !UserQuestions.TryFormatAnswers(card, answer.Answers, out userText))
            {
                throw new InvalidOperationException("The question is no longer available for this answer.");
            }
        }
        else if (regenerateAssistantMessageId is null &&
            UserQuestions.IsReservedAnswerMessageId(thread, runOptions.Projection.UserMessageId))
        {
            throw new InvalidOperationException("This message ID is reserved for question answers.");
        }

        var ownerDefinition = userScope.AppService.ResolveAgentDefinition(
            AgentSelectionResolver.ResolveAgentId(agentId, thread.AgentId, thread.DisplayMessages.Count > 0));
        var chatModelProviderId = AgentSelectionResolver.ResolveModelProviderId(ownerDefinition, ownerDefinition,
            requestedModelProviderId, userScope.ModelProviderSettings.SelectedChatModelProviderId?.ToString());
        var recipientId = forwardedProps is { ValueKind: JsonValueKind.Object } recipientProperties &&
            recipientProperties.TryGetProperty("recipientAgentId", out var recipientValue) && recipientValue.ValueKind == JsonValueKind.String
                ? recipientValue.GetString() : null;
        if (runOptions.ContinueSession) { recipientId = ChatParticipantSessions.Read(thread.SessionElement).LastRecipientId; }

        var agentDefinition = await AgentSelectionResolver.ResolveRecipientAsync(userScope, thread, ownerDefinition, recipientId, ct).ConfigureAwait(false);
        modelProviderId = AgentSelectionResolver.ResolveModelProviderId(ownerDefinition, agentDefinition, chatModelProviderId,
            userScope.ModelProviderSettings.SelectedChatModelProviderId?.ToString());
        await using var preparationContextScope = runContextAccessor.SetContext(new RunContext
        {
            UserId = userId,
            ExecutionKind = RunExecutionKind.WebChat,
            ExecutionId = thread.Id,
            ProjectId = thread.ProjectId,
            IsIncognito = thread.IsIncognito,
            StandardRunDirectories = projectDirectories,
            AllowedDirectories = userScope.CrewService.ResolveAllowedDirectories(userId, agentDefinition, projectDirectories),
            AirGapped = userScope.PrivacySettings.IsLocalOnly || ownerDefinition.IsLocalOnly || agentDefinition.IsLocalOnly,
            ExecutionProviderId = shellExecutionMode,
            ConfiguredExecutionProviderId = configuredProviderId,
            PermissionPreset = preset,
            ExecutionDevice = executionDevice,
            CurrentAgentName = agentDefinition.Name,
            CurrentAgentId = agentDefinition.Id,
            QuestionAgentId = runOptions.AllowUserQuestions ? agentDefinition.Id : null
        });
        userScope.AppService.SyncTools();
        var selectedProvider = userScope.AppService.ResolveChatModelProvider(agentDefinition, modelProviderId);
        var clientResolution = await userScope.AppService.GetChatClientResolutionAsync(
            agentDefinition,
            selectedProvider).ConfigureAwait(false);
        var chatClient = clientResolution.Client;
        var effectiveProvider = clientResolution.EffectiveProvider;

        var workspaceFiles = forwardedProps is { ValueKind: JsonValueKind.Object } properties &&
            properties.TryGetProperty("workspaceFiles", out var files)
                ? files.Deserialize<WorkspaceFileReference[]>(JsonSerializerOptions.Web) ?? [] : [];
        if (!ChatAttachmentLimits.AreWorkspaceFilesValid(workspaceFiles)) { throw new ArgumentException("Invalid workspace file references."); }

        var attachments = await ResolveAttachmentsAsync(userId, threadId, attachmentIds, thread.IsIncognito, ct).ConfigureAwait(false);
        if (!runOptions.ContinueSession && string.IsNullOrWhiteSpace(userText) && attachments.Count == 0 && workspaceFiles.Length == 0)
        {
            throw new ArgumentException("A chat run requires either message text or at least one attachment.");
        }

        var contextAttachments = agentDefinition.Id == ownerDefinition.Id ? attachments :
            thread.DisplayMessages.SelectMany(message => message.Attachments).Concat(attachments)
                .DistinctBy(attachment => attachment.Id).Where(attachment =>
                    chatAttachmentService.Get(userId, threadId, attachment.Id, thread.IsIncognito) is not null).ToArray();
        var runtimeUserText = attachmentManifestBuilder.Compose(
            userText, contextAttachments, project?.NodeId is not null);
        runtimeUserText = ChatDisplayMessageFormatter.ComposeFiles(runtimeUserText, workspaceFiles);
        var observerAccessor = new AgentRunObserverAccessor();
        var toolObserverAccessor = new ToolTrackingObserverAccessor();

        if (!runOptions.ContinueSession)
        {
            var promptDecision = await runtimeHookService.DispatchUserPromptSubmitAsync(new PromptHookContext
            {
                UserId = userId,
                AgentId = agentDefinition.Id,
                ChatThreadId = thread.Id,
                ExecutionKind = HookExecutionKinds.WebChat,
                PromptText = ChatDisplayMessageFormatter.ComposeFiles(userText, workspaceFiles),
                Attachments = MapAttachments(attachments)
            }, ct).ConfigureAwait(false);

            if (!promptDecision.IsAllowed)
            {
                throw new HookDeniedException(
                    HookEventKind.UserPromptSubmit,
                    toolName: null,
                    promptDecision.Message ?? "User prompt was denied by a hook.",
                    promptDecision.Reason);
            }
        }

        var extraProviders = new List<AIContextProvider>();
        if (runOptions.Goal is { } goal)
        {
            extraProviders.Add(new GoalContextProvider(userScope.ConversationGoals, userId, thread, goal.Id,
                new GoalToolset(userScope.ConversationGoals, userId, thread, goal.Id, effectiveRunId,
                    agentDefinition.Id, runGuard, runContextAccessor)));
        }

        if (effectiveProvider.SupportsImages)
        {
            extraProviders.Add(new VisionToolProvider(
                chatClient,
                () => runContextAccessor.Current?.AllowedDirectories ?? []));
        }

        if (await CreateAvatarMarkupProviderAsync(userScope, ct).ConfigureAwait(false) is { } avatarMarkupProvider)
        {
            extraProviders.Add(avatarMarkupProvider);
        }

        var innerAgent = await CreateObservedAgentAsync(userScope, agentDefinition, modelProviderId,
            observerAccessor, toolObserverAccessor, extraProviders, runOptions.Goal is not null).ConfigureAwait(false);

        var resolvedChatOptions = ResolveChatOptions(selectedProvider);
        var innerRunOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = resolvedChatOptions
        };

        if (isNewThread)
        {
            await userScope.ChatThreadRepository.CreateThreadAsync(userId, thread, ct: ct).ConfigureAwait(false);
        }

        return new RunPreparation
        {
            Goal = runOptions.Goal,
            AllowUserQuestions = runOptions.AllowUserQuestions,
            ContinueSession = runOptions.ContinueSession,
            UserScope = userScope,
            ChatClient = chatClient,
            EffectiveProviderId = effectiveProvider.Id,
            InnerAgent = innerAgent,
            AgentDefinition = agentDefinition,
            OwnerDefinition = ownerDefinition,
            RequestedModelProviderId = requestedModelProviderId,
            ChatModelProviderId = chatModelProviderId,
            Thread = thread,
            UserText = userText,
            RuntimeUserText = runtimeUserText,
            Attachments = attachments,
            WorkspaceFiles = workspaceFiles,
            RunOptions = innerRunOptions,
            RunId = effectiveRunId,
            ExecutionProviderId = shellExecutionMode,
            ConfiguredExecutionProviderId = configuredProviderId,
            PermissionPreset = preset,
            ExecutionDevice = executionDevice,
            ApprovalRunMode = approvalRunMode,
            RegenerateAssistantMessageId = regenerateAssistantMessageId,
            ObserverAccessor = observerAccessor,
            ToolObserverAccessor = toolObserverAccessor,
            Projection = runOptions.Projection
        };
    }

    /// <summary>Builds web agents with the same observation and tool-hook boundaries for direct and shared turns.</summary>
    private Task<ChatClientAgent> CreateObservedAgentAsync(
        UserServiceScope userScope,
        AgentDefinition definition,
        string? modelProviderId,
        AgentRunObserverAccessor observer,
        ToolTrackingObserverAccessor toolObserver,
        IEnumerable<AIContextProvider>? extraProviders = null,
        bool bypassCache = false) =>
        userScope.AppService.GetOrCreateAgent(
            userScope.UserId, definition, modelProviderId,
            configureBuilder: builder => builder.UseAgentRunObserver(observer).UseToolTracking(toolObserver),
            extraProviders: extraProviders,
            bypassRequestedAgentCache: bypassCache,
            configureAgentBuilder: (agent, builder) => builder.UseAgentToolHooks(
                runtimeHookService, runContextAccessor, agent.Id, userScope.ToolGovernanceSettings,
                quotaUsageStore, userScope.CrewService.Tools.GetNetworkScope, logger));

    // Teaches emotion tags only when the reply can reach a connected avatar with a known catalog.
    private async Task<AIContextProvider?> CreateAvatarMarkupProviderAsync(UserServiceScope userScope, CancellationToken ct)
    {
        var targets = SpeechOutputPolicy.ResolveTargets(userScope.IsInRole("Admin"), userScope.SpeechSettings.AssistantSpeechOutputMode);
        if (!SpeechOutputPolicy.IncludesAvatar(targets) ||
            !await avatarRuntimeSettings.EnsureReadyAsync().ConfigureAwait(false))
        {
            return null;
        }

        var catalog = await avatarBridgeService.ListEmotionsAsync().ConfigureAwait(false);
        if (!catalog.Success || catalog.Emotions.Count == 0)
        {
            return null;
        }

        var template = await userScope.CrewService.ReadPromptInstructionAsync(userScope.UserId, PromptFileNames.AvatarMarkupPrompt, ct).ConfigureAwait(false)
            ?? SeedContentProvider.GetInstruction(PromptFileNames.AvatarMarkupPrompt);
        var emotionNames = string.Join(", ", catalog.Emotions);
        return new AgentPersonalContextProvider(template, _ => Task.FromResult<object>(new { emotion_names = emotionNames }));
    }

    private Task NotifyThreadChangedAsync(string userId)
    {
        return chatThreadNotifications.NotifyAsync(userId, new ChatThreadChanged());
    }

    private static (string? ThreadId, string? RunId, JsonElement? ForwardedProps) ExtractAgUiInput(ChatOptions? chatOptions)
    {
        if (chatOptions is null || !chatOptions.TryGetRunAgentInput(out var input))
        {
            return (null, null, null);
        }

        return (input.ThreadId, input.RunId, input.ForwardedProperties);
    }

    private static (
        string? ModelProviderId,
        string? AgentId,
        IReadOnlyList<string> AttachmentIds,
        ToolApprovalRunMode ApprovalRunMode,
        string? RegenerateAssistantMessageId) ParseForwardedProps(
        JsonElement? forwardedProps)
    {
        if (forwardedProps is not { ValueKind: JsonValueKind.Object } element)
        {
            return (null, null, Array.Empty<string>(), ToolApprovalRunMode.UseAgentPolicy, null);
        }

        string? modelProviderId = null;
        string? agentId = null;
        List<string> attachmentIds = [];
        var approvalRunMode = ToolApprovalRunMode.UseAgentPolicy;
        string? regenerateAssistantMessageId = null;

        if (element.TryGetProperty("modelProviderId", out var mp) && mp.ValueKind == JsonValueKind.String)
        {
            modelProviderId = mp.GetString();
        }

        if (element.TryGetProperty("agentId", out var ag) && ag.ValueKind == JsonValueKind.String)
        {
            agentId = ag.GetString();
        }

        if (element.TryGetProperty("attachmentIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ids.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } id && !string.IsNullOrWhiteSpace(id))
                {
                    attachmentIds.Add(id);
                }
            }
        }

        if (element.TryGetProperty("approvalRunMode", out var ar) && ar.ValueKind == JsonValueKind.String &&
            Enum.TryParse<ToolApprovalRunMode>(ar.GetString(), ignoreCase: true, out var parsedMode))
        {
            approvalRunMode = parsedMode;
        }

        if (element.TryGetProperty("regenerateAssistantMessageId", out var regenerate) &&
            regenerate.ValueKind == JsonValueKind.String)
        {
            regenerateAssistantMessageId = regenerate.GetString();
        }

        return (modelProviderId, agentId, attachmentIds, approvalRunMode, regenerateAssistantMessageId);
    }

    private static RegenerationState? ResolveLatestRegeneration(
        RunPreparation prep,
        AgentSession? session, bool sessionRewound)
    {
        if (prep.RegenerateAssistantMessageId is null)
        {
            return null;
        }

        ChatDisplayMessage? userMessage = null;
        var userIndex = -1;
        lock (prep.Thread.DisplayMessages)
        {
            var targetIndex = prep.Thread.DisplayMessages.Count - 1;
            if (targetIndex < 1 ||
                prep.Thread.DisplayMessages[targetIndex].From != MessageOwner.Bot ||
                prep.Thread.DisplayMessages[targetIndex].IsInteractionResponse ||
                !string.Equals(
                    prep.Thread.DisplayMessages[targetIndex].MessageId,
                    prep.RegenerateAssistantMessageId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The response is no longer the latest completed response.");
            }

            for (var index = targetIndex - 1; index >= 0; index--)
            {
                var candidate = prep.Thread.DisplayMessages[index];
                if (candidate.From == MessageOwner.User && !candidate.IsInteractionResponse)
                {
                    userMessage = candidate;
                    userIndex = index;
                    break;
                }
            }

            if (userMessage is null ||
                !string.Equals(
                    userMessage.MessageId,
                    prep.Projection.UserMessageId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The response no longer belongs to the requested user turn.");
            }
        }

        if (sessionRewound)
        {
            var restored = session!.TryGetInMemoryChatHistory(out var retained) ? retained : [];
            return new RegenerationState(userMessage, userIndex, restored, restored.Count);
        }

        if (session is null || !session.TryGetInMemoryChatHistory(out var history))
        {
            throw new InvalidOperationException("The response session history is unavailable.");
        }

        var historyStartIndex = history.FindLastIndex(message =>
            message.IsSubmittedRequest() && !string.IsNullOrWhiteSpace(message.Text) &&
            message.AdditionalProperties?.ContainsKey(ContinuationMarkerKey) != true);
        if (historyStartIndex < 0 ||
            !string.Equals(
                history[historyStartIndex].Text,
                prep.RuntimeUserText,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The response user turn is missing from session history.");
        }

        return new RegenerationState(userMessage, userIndex, history, historyStartIndex);
    }

    private Task<IReadOnlyList<ChatAttachment>> ResolveAttachmentsAsync(
        string userId,
        string threadId,
        IReadOnlyList<string> attachmentIds,
        bool incognito,
        CancellationToken ct)
    {
        _ = ct;
        if (attachmentIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ChatAttachment>>(Array.Empty<ChatAttachment>());
        }

        var resolved = new List<ChatAttachment>(attachmentIds.Count);
        foreach (var id in attachmentIds)
        {
            var attachment = chatAttachmentService.Get(userId, threadId, id, incognito);
            if (attachment is not null)
            {
                resolved.Add(attachment);
            }
            else
            {
                return Task.FromException<IReadOnlyList<ChatAttachment>>(
                    new InvalidOperationException($"Attachment '{id}' is no longer available for this chat thread."));
            }
        }

        return Task.FromResult<IReadOnlyList<ChatAttachment>>(resolved);
    }

    private static IReadOnlyList<ChatAttachmentInfo> MapAttachments(IReadOnlyList<ChatAttachment> attachments)
    {
        return attachments
            .Select(attachment => new ChatAttachmentInfo
            {
                Id = attachment.Id,
                DisplayName = attachment.DisplayName,
                MediaType = attachment.MediaType,
                SizeBytes = attachment.SizeBytes,
                Kind = attachment.Kind
            })
            .ToArray();
    }

    private static string ExtractUserText(IEnumerable<ChatMessage> messages)
    {
        // AG-UI delivers the new user turn as the trailing user message in the request body.
        var userMessage = messages.LastOrDefault(m => m.Role == ChatRole.User);
        return userMessage?.Text ?? string.Empty;
    }

    private static ChatOptions ResolveChatOptions(ModelProviderConfig modelProvider)
    {
        // The thinking default is applied by the provider (OllamaModelProvider) so it reaches every
        // run path uniformly; setting it here would override per-call values and defeat that.
        return modelProvider.ChatOptions.Clone();
    }

    private static bool NeedsTitleGeneration(ChatThread thread)
    {
        if (string.IsNullOrWhiteSpace(thread.Title))
        {
            return true;
        }

        var firstUserMessage = thread.DisplayMessages.FirstOrDefault(m => m.From == MessageOwner.User);
        if (firstUserMessage is null)
        {
            return false;
        }

        return string.Equals(thread.Title, ChatDisplayMessageFormatter.BuildPlaceholderTitle(firstUserMessage.Text, firstUserMessage.Attachments, firstUserMessage.WorkspaceFiles), StringComparison.Ordinal);
    }

    private void QueueTitleGenerationIfNeeded(RunPreparation prep, Func<Task>? onCompleted)
    {
        var userId = prep.UserScope.UserId;
        var threadId = prep.Thread.Id;
        var messageCount = prep.Thread.DisplayMessages.Count;
        var isEnabled = prep.UserScope.UiSettings.IsChatTitleGenerationEnabled;
        var needsTitle = NeedsTitleGeneration(prep.Thread);

        if (!isEnabled ||
            !needsTitle ||
            messageCount < TitleGenerationMinimumMessageCount)
        {
            return;
        }

        var key = $"{userId}:{threadId}";
        if (!_queuedTitleGenerations.TryAdd(key, 0))
        {
            return;
        }

        var userScope = prep.UserScope;
        var chatClient = prep.ChatClient;
        var previousMessages = BuildTitleConversationText(prep.Thread);

        _ = Task.Run(async () =>
        {
            var titleUsage = new UsageLedger();
            try
            {
                if (!await budgets.AdmitAsync(userId, prep.EffectiveProviderId).ConfigureAwait(false)) { return; }

                await using var titleScope = runContextAccessor.SetContext(new RunContext
                {
                    UserId = userId, ExecutionKind = RunExecutionKind.WebChat, ExecutionId = string.Empty,
                    UsageLedger = titleUsage, AirGapped = userScope.PrivacySettings.IsLocalOnly || prep.AgentDefinition.IsLocalOnly
                });
                var title = await GenerateTitleAsync(
                    chatClient,
                    userId,
                    previousMessages,
                    CancellationToken.None).ConfigureAwait(false);

                // Eligibility and metadata persistence share the same lease as manual chat mutations.
                using var titleLease = await runGuard.AcquireAsync(userId, threadId,
                    $"title_{Guid.NewGuid():N}").ConfigureAwait(false);
                var latestThread = await userScope.ChatThreadRepository
                    .GetThreadAsync(userId, threadId, CancellationToken.None)
                    .ConfigureAwait(false);

                if (latestThread is null ||
                    !userScope.UiSettings.IsChatTitleGenerationEnabled ||
                    !NeedsTitleGeneration(latestThread))
                {
                    return;
                }

                // The shared rename path updates only a thread that still has an authoritative owner record.
                await chatSessionService
                    .RenameThreadAsync(userId, threadId, title, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Title generation failed for thread {ThreadId}.", threadId);
            }
            finally
            {
                if (titleUsage.Snapshot() is { Count: > 0 } usage)
                {
                    try
                    {
                        var metrics = MetricsEventBuilder.Anonymous(usage, MetricsEventBuilder.BackgroundBucket);
                        await metricsEventStore.AppendAsync(userId, metrics).ConfigureAwait(false);
                        await budgets.NotifyCompletedAsync(userId, metrics).ConfigureAwait(false);
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not save title usage for provider {ProviderId}.", prep.EffectiveProviderId); }
                }

                _queuedTitleGenerations.TryRemove(key, out _);
                // The title lease can win the slot just released by normal run completion.
                if (onCompleted is not null)
                {
                    try { await onCompleted().ConfigureAwait(false); }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not continue conversation {ThreadId} after title generation.", threadId); }
                }
            }
        }, CancellationToken.None);
    }

    private static bool IsElicitationPseudoUpdate(AgentResponseUpdate update)
    {
        if (update.Contents is null)
        {
            return false;
        }

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent fc when fc.CallId.StartsWith("elicit_", StringComparison.Ordinal):
                case FunctionResultContent fr when fr.CallId.StartsWith("elicit_", StringComparison.Ordinal):
                    return true;
            }
        }

        return false;
    }

    private static bool IsStructuralUpdate(AgentResponseUpdate update) =>
        update.Contents?.Any(content =>
            content is FunctionCallContent or FunctionResultContent or ToolApprovalRequestContent) == true;

    private static bool IsElicitationCallUpdate(AgentResponseUpdate update)
    {
        if (update.Contents is null)
        {
            return false;
        }

        foreach (var content in update.Contents)
        {
            if (content is FunctionCallContent fc && fc.CallId.StartsWith("elicit_", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static AgentResponseUpdate BuildElicitationCallUpdate(ElicitationRequest request, string callId)
    {
        var toolName = request.SchemaType switch
        {
            ElicitationSchemaType.Boolean => "request_boolean_input",
            ElicitationSchemaType.String => "request_string_input",
            ElicitationSchemaType.Number => "request_number_input",
            ElicitationSchemaType.Enum => "request_enum_input",
            _ => "request_string_input"
        };

        var arguments = new Dictionary<string, object?>
        {
            ["requestId"] = request.RequestId.ToString(),
            ["message"] = request.Message,
            ["schemaType"] = request.SchemaType.ToString(),
            ["request"] = JsonSerializer.SerializeToElement(request)
        };

        return new AgentResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent(callId, toolName, arguments)]);
    }

    private static AgentResponseUpdate BuildElicitationResultUpdate(string callId, ChatDisplayMessage? userResponseMessage)
    {
        object result = userResponseMessage is null
            ? "submitted"
            : new { userResponseMessage };

        return new AgentResponseUpdate(ChatRole.Tool,
            [new FunctionResultContent(callId, result)]);
    }

    private static List<ToolApprovalRequestContent> ExtractApprovalRequests(AgentResponseUpdate update)
    {
        var requests = new List<ToolApprovalRequestContent>();
        if (update.Contents is null)
        {
            return requests;
        }

        foreach (var content in update.Contents)
        {
            if (content is ToolApprovalRequestContent request)
            {
                requests.Add(request);
            }
        }

        return requests;
    }

    private static AgentResponseUpdate? WithoutApprovalRequests(AgentResponseUpdate update)
    {
        if (update.Contents is null || update.Contents.Count == 0)
        {
            return null;
        }

        var remaining = update.Contents.Where(c => c is not ToolApprovalRequestContent).ToList();
        if (remaining.Count == 0)
        {
            return null;
        }

        if (remaining.Count == update.Contents.Count)
        {
            return update;
        }

        var chatUpdate = update.AsChatResponseUpdate();
        return new AgentResponseUpdate(new ChatResponseUpdate
        {
            Role = chatUpdate.Role,
            Contents = remaining,
            MessageId = chatUpdate.MessageId,
            AuthorName = chatUpdate.AuthorName,
            CreatedAt = chatUpdate.CreatedAt,
            RawRepresentation = chatUpdate.RawRepresentation,
            ResponseId = chatUpdate.ResponseId,
            AdditionalProperties = chatUpdate.AdditionalProperties
        })
        {
            AgentId = update.AgentId,
            ContinuationToken = update.ContinuationToken
        };
    }

    private static AgentResponseUpdate EnsureAssistantMessageId(AgentResponseUpdate update, ChatDisplayMessage botMessage)
    {
        if (update.Role != ChatRole.Assistant || !string.IsNullOrWhiteSpace(update.MessageId))
        {
            return update;
        }

        // Some streaming providers currently omit MessageId on assistant deltas. AG-UI can
        // synthesize one for text, but reasoning events need a real id before they reach SSE.
        update.MessageId = botMessage.MessageId;
        if (update.RawRepresentation is ChatResponseUpdate chatUpdate &&
            string.IsNullOrWhiteSpace(chatUpdate.MessageId))
        {
            chatUpdate.MessageId = botMessage.MessageId;
        }

        return update;
    }

    private static bool IsToolApprovalCallUpdate(AgentResponseUpdate update)
    {
        if (update.Contents is null)
        {
            return false;
        }

        foreach (var content in update.Contents)
        {
            if (content is FunctionCallContent fc && fc.CallId.StartsWith(ToolApprovalCallIdPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsToolApprovalPseudoUpdate(AgentResponseUpdate update)
    {
        if (update.Contents is null)
        {
            return false;
        }

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent fc when fc.CallId.StartsWith(ToolApprovalCallIdPrefix, StringComparison.Ordinal):
                case FunctionResultContent fr when fr.CallId.StartsWith(ToolApprovalCallIdPrefix, StringComparison.Ordinal):
                    return true;
            }
        }

        return false;
    }

    private static AgentResponseUpdate BuildToolApprovalCallUpdate(ToolApprovalRequest request, string callId)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["requestId"] = request.RequestId,
            ["toolName"] = request.ToolName,
            ["message"] = request.Message,
            ["request"] = JsonSerializer.SerializeToElement(request)
        };

        return new AgentResponseUpdate(ChatRole.Assistant,
            [new FunctionCallContent(callId, ToolApprovalToolName, arguments)]);
    }

    private static AgentResponseUpdate BuildToolApprovalResultUpdate(string callId, ChatDisplayMessage? userResponseMessage)
    {
        object result = userResponseMessage is null
            ? "submitted"
            : new { userResponseMessage };

        return new AgentResponseUpdate(ChatRole.Tool,
            [new FunctionResultContent(callId, result)]);
    }

    private static string BuildTitleConversationText(ChatThread thread)
    {
        ChatDisplayMessage[] messages;
        lock (thread.DisplayMessages)
        {
            messages = [.. thread.DisplayMessages];
        }

        return string.Join(
            "\n\n",
            messages
                .Select(message => ChatDisplayMessageFormatter.GetConversationLine(message))
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private async Task<string> GenerateTitleAsync(
        IChatClient chatClient,
        string userId,
        string previousMessages,
        CancellationToken ct)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var template = await userScope.CrewService.ReadPromptInstructionAsync(
            userId, PromptFileNames.ChatTitlePrompt, ct).ConfigureAwait(false)
            ?? SeedContentProvider.GetInstruction(PromptFileNames.ChatTitlePrompt);
        var instructions = PromptTemplateRenderer.Render(template, new { chat_history = previousMessages });
        var titleAgentOptions = new ChatClientAgentOptions
        {
            Id = "title-agent",
            Name = "TitleAgent"
        };
        var titleAgent = chatClient.AsBuilder().BuildAIAgent(titleAgentOptions);
        var titleSession = await titleAgent.CreateSessionAsync(ct).ConfigureAwait(false);

        var titleRunOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                MaxOutputTokens = 24,
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.None
                },
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["think"] = false
                }
            }
        };

        var result = await titleAgent.RunAsync(
            new ChatMessage(ChatRole.User, instructions),
            titleSession,
            titleRunOptions,
            ct).ConfigureAwait(false);

        // Models often wrap the title in quotes or end it like a sentence.
        var title = result.Text.Trim().TrimEnd('.').Trim('"', '\'', '“', '”').TrimEnd('.').Trim();
        return title.Length == 0 ? "New Chat" : title;
    }

    /// <summary>Forwards invocation timing while stream results retain terminal status ownership.</summary>
    private sealed class ToolExecutionObserver(Action<FunctionCallContent, DateTimeOffset?> onChanged) : IToolTrackingObserver
    {
        /// <inheritdoc />
        public void OnToolStarted(FunctionCallContent functionCall) => onChanged(functionCall, null);
        /// <inheritdoc />
        public void OnToolCompleted(FunctionCallContent functionCall) => onChanged(functionCall, DateTimeOffset.UtcNow);
        /// <inheritdoc />
        public void OnToolFailed(FunctionCallContent functionCall) => onChanged(functionCall, DateTimeOffset.UtcNow);
    }

    private sealed class RunPreparation
    {
        public ChatGoal? Goal { get; init; }
        public bool AllowUserQuestions { get; init; }
        public bool ContinueSession { get; init; }
        public required UserServiceScope UserScope { get; init; }
        public required IChatClient ChatClient { get; init; }
        public required Guid EffectiveProviderId { get; init; }
        public required ChatClientAgent InnerAgent { get; init; }
        public required AgentDefinition AgentDefinition { get; init; }
        public required AgentDefinition OwnerDefinition { get; init; }
        public string? RequestedModelProviderId { get; init; }
        public string? ChatModelProviderId { get; init; }
        public required ChatThread Thread { get; init; }
        public required string UserText { get; init; }
        public required string RuntimeUserText { get; init; }
        public required IReadOnlyList<ChatAttachment> Attachments { get; init; }
        public required IReadOnlyList<WorkspaceFileReference> WorkspaceFiles { get; init; }
        public required ChatClientAgentRunOptions RunOptions { get; init; }
        public required string RunId { get; init; }
        public required string? ExecutionProviderId { get; init; }
        public required ToolApprovalRunMode ApprovalRunMode { get; init; }
        public string? ConfiguredExecutionProviderId { get; init; }
        public ChatPermissionPreset PermissionPreset { get; init; }
        public string? ExecutionDevice { get; init; }
        public string? RegenerateAssistantMessageId { get; init; }
        public required AgentRunObserverAccessor ObserverAccessor { get; init; }
        public required ToolTrackingObserverAccessor ToolObserverAccessor { get; init; }
        public required ChatRunProjection Projection { get; init; }
    }

    private sealed record RegenerationState(
        ChatDisplayMessage UserMessage,
        int DisplayStartIndex,
        List<ChatMessage> History,
        int HistoryStartIndex);

}
