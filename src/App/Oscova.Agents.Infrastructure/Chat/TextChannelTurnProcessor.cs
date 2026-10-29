using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Conversations;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.ToolGovernance;
using Oscova.Agents.Hooks;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Infrastructure.Hooks;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Metrics;
using Oscova.Agents.Middleware;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Executes the persisted agent turn shared by text messaging channels.</summary>
public sealed class TextChannelTurnProcessor(
    Oscova.Agents.Secrets.IContentProtector contentProtector,
    ILogger<TextChannelTurnProcessor> logger,
    RunContextAccessor runContextAccessor,
    IAgentRuntimeHookService runtimeHookService,
    IChatAttachmentService chatAttachmentService,
    ChatAttachmentManifestBuilder attachmentManifestBuilder,
    IMetricsEventStore metricsEventStore,
    IEgressAuditStore egressAuditStore,
    ThreadRunGuard threadRunGuard,
    IToolQuotaUsageStore quotaUsageStore,
    ChatSteeringRegistry steeringRegistry,
    ModelBudgetService budgets,
    ChannelConversationBindings bindings)
{
    private const string RunFailureReply = "There was an error generating a response.";

    /// <summary>Admits one channel message and runs its selected agent.</summary>
    public async Task<string> ProcessAsync(TextChannelTurnInput input, CancellationToken ct = default)
    {
        if (!contentProtector.IsAvailable(input.OwnerUserId))
        {
            return "Unlock your Vault Password in Oscova before continuing.";
        }
        // A text message sent during a live run on the current thread joins that run without a reply.
        if (await TrySteerAsync(input, ct).ConfigureAwait(false))
        {
            return string.Empty;
        }

        await input.UserScope.TextChannelRunGate.WaitAsync(ct).ConfigureAwait(false);
        var runCancellationToken = ct;
        try
        {
            return await ProcessCoreAsync(input, ct, token => runCancellationToken = token).ConfigureAwait(false);
        }
        catch (HookDeniedException)
        {
            throw;
        }
        catch (OperationCanceledException) when (runCancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing {Channel} message from {SenderId} for user '{UserId}'.",
                input.Channel, input.SenderId, input.OwnerUserId);
            return RunFailureReply;
        }
        finally
        {
            input.UserScope.TextChannelRunGate.Release();
        }
    }

    private async Task<bool> TrySteerAsync(TextChannelTurnInput input, CancellationToken ct)
    {
        if (IsNewConversation(input.Text) || input.RawAttachments.Count > 0 || string.IsNullOrWhiteSpace(input.Text))
        {
            return false;
        }

        var (_, thread) = await ResolveConversationAsync(input, create: false, ct).ConfigureAwait(false);
        if (thread is null)
        {
            return false;
        }

        var message = new ChatDisplayMessage
        {
            MessageId = $"msg_{Guid.NewGuid():N}",
            Channel = input.Route.HookKind,
            From = MessageOwner.User,
            Text = input.Text
        };
        return await steeringRegistry.TrySteerAsync(
            input.OwnerUserId,
            thread.Id,
            message,
            input.Route.HookKind,
            input.Route.ChannelId,
            ct).ConfigureAwait(false) is not null;
    }

    private static async Task<AgentDefinition> ResolveAgentDefinitionAsync(TextChannelTurnInput input)
    {
        var appService = input.UserScope.AppService;
        await appService.ReloadAgentDefinitionsAsync().ConfigureAwait(false);
        return appService.ResolveAgentDefinition(input.Route.AgentId);
    }

    internal static bool IsNewConversation(string text)
        => string.Equals(text.Trim(), "/new", StringComparison.OrdinalIgnoreCase);

    // Both live steering and idle turns resolve the notification destination before web selection.
    private async Task<(AgentDefinition Agent, ChatThread? Thread)> ResolveConversationAsync(
        TextChannelTurnInput input, bool create, CancellationToken ct)
    {
        var selected = await ResolveAgentDefinitionAsync(input).ConfigureAwait(false);
        var scope = input.UserScope;
        var boundId = bindings.GetThreadId(input.OwnerUserId, input.Route.ChannelId);
        var bound = boundId is null ? null
            : await scope.ChatThreadRepository.GetThreadAsync(input.OwnerUserId, boundId, ct).ConfigureAwait(false);
        if (bound is not null)
        {
            if (bound.ProjectId is not null) { await scope.ProjectAccess.AuthorizeAsync(bound.ProjectId, ct).ConfigureAwait(false); }

            return (scope.AppService.ResolveAgentDefinition(bound.AgentId), bound);
        }

        var thread = create
            ? await scope.CurrentChatThreadService.GetOrCreateCurrentThreadAsync(selected.Id, null, ct).ConfigureAwait(false)
            : await scope.CurrentChatThreadService.GetCurrentThreadAsync(selected.Id, null, ct).ConfigureAwait(false);
        return (selected, thread);
    }

    private async Task<string> ProcessCoreAsync(TextChannelTurnInput input, CancellationToken ct,
        Action<CancellationToken> onRunCancellation)
    {
        var ownerUserId = input.OwnerUserId;
        var userScope = input.UserScope;
        var currentChatThreadService = userScope.CurrentChatThreadService;
        var chatThreadRepository = userScope.ChatThreadRepository;
        var appService = userScope.AppService;
        var channel = input.Route;
        if (IsNewConversation(input.Text))
        {
            // Consume the shared command before it can steer or become model input.
            bindings.Clear(ownerUserId, channel.ChannelId);
            var selected = await ResolveAgentDefinitionAsync(input).ConfigureAwait(false);
            await currentChatThreadService.StartNewConversationAsync(selected.Id, null, ct).ConfigureAwait(false);
            return "Started a new conversation.";
        }

        var (agentDefinition, resolvedThread) = await ResolveConversationAsync(input, create: true, ct).ConfigureAwait(false);
        var thread = resolvedThread!;
        var runId = Guid.NewGuid().ToString("N");
        // A message that could not steer waits for the live run on this thread to finish.
        using var runLease = await threadRunGuard.AcquireAsync(ownerUserId, thread.Id, runId, ct).ConfigureAwait(false);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, runLease.CancellationToken);
        onRunCancellation(runCancellation.Token);
        var replacedWhileWaiting = false;
        var origin = input.Origin with
        {
            CancelRun = () =>
            {
                replacedWhileWaiting = true;
                threadRunGuard.CancelExact(ownerUserId, thread.Id, runId);
            }
        };
        thread = await chatThreadRepository.GetThreadAsync(ownerUserId, thread.Id).ConfigureAwait(false) ?? thread;
        var messageText = input.Text;
        var rawAttachments = input.RawAttachments.Take(ChatAttachmentLimits.MaxFilesPerMessage).ToArray();
        var airGapped = userScope.PrivacySettings.IsLocalOnly || agentDefinition.IsLocalOnly;
        string? shellExecutionMode = userScope.ExecutionSettings.ProviderId;
        var projectDirectories = userScope.ProjectAccess.ResolveRunDirectories(thread.ProjectId, shellExecutionMode);
        var egressLedger = new EgressLedger();
        var usageLedger = new UsageLedger();
        var toolInvocationLedger = new ToolInvocationLedger();
        var fallbackSignal = new FallbackActivationSignal();
        IReadOnlyList<AllowedDirectory> preflightDirectories = ChatRunAccess.BuildStandardRunDirectories(
            chatAttachmentService,
            ownerUserId,
            thread).Concat(projectDirectories).ToArray();
        var preflightRunContext = CreateRunContext(preflightDirectories);
        var stagedAttachments = await ChannelAttachmentPreflight.StageAfterAuthorizationAsync(
            runtimeHookService,
            runContextAccessor,
            preflightRunContext,
            new PromptHookContext
            {
                UserId = ownerUserId,
                AgentId = agentDefinition.Id,
                ChatThreadId = thread.Id,
                ExecutionKind = channel.HookKind,
                ChannelId = channel.ChannelId,
                PromptText = messageText,
                Attachments = rawAttachments
            },
            () => input.StageAttachmentsAsync(thread.Id, runCancellation.Token),
            runCancellation.Token).ConfigureAwait(false);

        if (stagedAttachments.Count == 0 && string.IsNullOrWhiteSpace(messageText))
        {
            return string.Empty;
        }

        var runtimeUserText = attachmentManifestBuilder.Compose(
            messageText, stagedAttachments);
        var userMessage = new ChatDisplayMessage
        {
            Channel = channel.HookKind,
            From = MessageOwner.User,
            Text = messageText,
            Attachments = stagedAttachments
        };
        IReadOnlyList<AllowedDirectory> standardRunDirectories = ChatRunAccess.BuildStandardRunDirectories(
            chatAttachmentService,
            ownerUserId,
            thread,
            userMessage).Concat(projectDirectories).ToArray();
        var context = new ElicitationContext
        {
            UserId = ownerUserId,
            Channel = input.Channel,
            ChannelMetadata = new TextChannelElicitationMetadata
            {
                ChannelOriginator = origin
            }
        };

        // This run scope gives tools and MCP infrastructure one app-owned source of truth
        // for the active channel execution identity.
        // Disposal drains this run's subagents before the channel lease is released.
        await using var delegatedWork = new DelegatedWorkState(runCancellation.Token);
        var runContext = CreateRunContext(standardRunDirectories, context).With(delegatedWork: delegatedWork);
        var runContextScope = runContextAccessor.SetContext(runContext);
        await using (runContextScope.ConfigureAwait(false))
        {

        try
        {
            var selectedProvider = appService.ResolveChatModelProvider(agentDefinition);
            // Initialization fallback may change which provider can inspect images.
            var (chatClient, effectiveProvider) = await appService.GetChatClientResolutionAsync(agentDefinition, selectedProvider).ConfigureAwait(false);
            if (!await budgets.AdmitAsync(ownerUserId, effectiveProvider.Id, context, runCancellation.Token).ConfigureAwait(false))
            {
                return await budgets.NoticeAsync(ownerUserId,
                    await budgets.GetAsync(ownerUserId, effectiveProvider.Id, runCancellation.Token).ConfigureAwait(false),
                    runCancellation.Token).ConfigureAwait(false);
            }

            // Only accepted work enters the saved conversation.
            if (string.IsNullOrWhiteSpace(thread.Title)) { thread.Title = channel.Title; }

            thread.AgentName = agentDefinition.Name;
            thread.DisplayMessages.Add(userMessage);
            await chatThreadRepository.SaveThreadAsync(ownerUserId, thread).ConfigureAwait(false);
            var chatOptions = selectedProvider.ChatOptions.Clone();
            var observerAccessor = new AgentRunObserverAccessor();
            var agent = await appService.GetOrCreateAgent(
                ownerUserId,
                agentDefinition,
                configureBuilder: builder => builder.UseAgentRunObserver(observerAccessor),
                extraProviders: effectiveProvider.SupportsImages
                    ? [new VisionToolProvider(
                        chatClient,
                        () => runContextAccessor.Current?.AllowedDirectories ?? [])]
                    : null,
                configureAgentBuilder: (definition, builder) => builder.UseAgentToolHooks(
                    runtimeHookService,
                    runContextAccessor,
                    definition.Id,
                    userScope.ToolGovernanceSettings,
                    quotaUsageStore,
                    userScope.CrewService.Tools.GetNetworkScope,
                    logger)).ConfigureAwait(false);

            var sessionResult = await AgentSessionRecovery.GetOrCreateAsync(
                agent,
                thread.SessionElement,
                logger,
                thread.Id,
                runCancellation.Token).ConfigureAwait(false);
            var session = sessionResult.Session;
            if (sessionResult.IsNew)
            {
                await runtimeHookService.DispatchSessionStartAsync(new SessionHookContext
                {
                    UserId = ownerUserId,
                    AgentId = agentDefinition.Id,
                    ChatThreadId = thread.Id,
                    ExecutionKind = channel.HookKind,
                    ChannelId = channel.ChannelId
                }, runCancellation.Token).ConfigureAwait(false);
            }

            var runOptions = new ChatClientAgentRunOptions { ChatOptions = chatOptions };
            var approvalContext = new ToolApprovalContext
            {
                UserId = ownerUserId,
                Channel = channel.ApprovalChannel,
                RunMode = ToolApprovalRunMode.UseAgentPolicy,
                ChannelMetadata = new TextChannelToolApprovalMetadata
                {
                    ChannelOriginator = origin
                }
            };
            var runLoop = new ToolApprovalRunLoop(userScope.ToolApprovalRouter);
            var steering = steeringRegistry.Register(
                ownerUserId,
                thread.Id,
                agentDefinition.Id,
                agent,
                session,
                runId,
                runContext,
                (message, _) => { thread.DisplayMessages.Add(message); return Task.CompletedTask; });
            AgentResponse result;
            try
            {
                observerAccessor.Observer = new ChatRunSessionObserver(thread, steering);
                // Stop requests cancel the lease, which must flow through approval and model execution.
                result = await runLoop.RunAsync(
                    agent,
                    new ChatMessage(ChatRole.User, runtimeUserText),
                    session,
                    runOptions,
                    approvalContext,
                    runCancellation.Token).ConfigureAwait(false);
                // A steer that arrived after the last model call still gets its own answer.
                if (await steering.CloseAsync(runCancellation.Token).ConfigureAwait(false))
                {
                    var followUp = await runLoop.RunAsync(
                        agent,
                        [],
                        session,
                        runOptions,
                        approvalContext,
                        runCancellation.Token).ConfigureAwait(false);
                    result = new AgentResponse([.. result.Messages, .. followUp.Messages]);
                }
            }
            catch (OperationCanceledException) when (runCancellation.Token.IsCancellationRequested)
            {
                if (replacedWhileWaiting)
                {
                    // Retain the cancelled prompt outcome before /new clears the current selection.
                    const string cancelledText = "Pending prompt cancelled by /new.";
                    thread.DisplayMessages.Add(new ChatDisplayMessage
                    {
                        Channel = channel.HookKind, From = MessageOwner.Bot,
                        Text = cancelledText,
                        Segments = [new ChatSegment(ChatSegmentType.Text, ChatSegmentState.Completed,
                            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, cancelledText, null)]
                    });
                    await chatThreadRepository.SaveThreadAsync(ownerUserId, thread).ConfigureAwait(false);
                }

                return string.Empty;
            }
            finally
            {
                steering.Dispose();
                observerAccessor.Observer = null;

                // Usage is captured after each completed model response, so persist it
                // even when a later round exits early.
                var metricsEvent = MetricsEventBuilder.BuildEvent(
                    runContext,
                    channel.MetricsSource,
                    usageLedger.Snapshot(),
                    toolInvocationLedger.Snapshot(),
                    fallbackSignal.Activated) with
                    {
                        RunId = runId
                    };
                try
                {
                    await metricsEventStore.AppendAsync(ownerUserId, metricsEvent, CancellationToken.None).ConfigureAwait(false);
                    await budgets.NotifyCompletedAsync(ownerUserId, metricsEvent).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to append metrics event for {Channel} run {RunId}.", input.Channel, runId);
                }
            }

            thread.SessionElement = await agent.SerializeSessionAsync(session).ConfigureAwait(false);
            var botSegment = new ChatSegment(
                ChatSegmentType.Text,
                ChatSegmentState.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                result.Text ?? string.Empty,
                null);

            var usage = usageLedger.Snapshot();
            var totals = MetricsEventBuilder.SumUsage(usage);
            var botMessage = new ChatDisplayMessage
            {
                Channel = channel.HookKind,
                From = MessageOwner.Bot,
                Text = result.Text ?? string.Empty,
                Segments = [botSegment]
            };
            if (usage.Count > 0)
            {
                botMessage.InputTokens = totals.Input;
                botMessage.OutputTokens = totals.Output;
                botMessage.TotalTokens = totals.Total;
            }

            thread.DisplayMessages.Add(botMessage);

            var replyText = result.Text;
            if (string.IsNullOrWhiteSpace(replyText))
            {
                logger.LogWarning(
                    "No response generated for {Channel} message from {SenderId} for user '{UserId}'.",
                    input.Channel, input.SenderId, ownerUserId);
                replyText = RunFailureReply;
            }

            await chatThreadRepository.SaveThreadAsync(ownerUserId, thread).ConfigureAwait(false);
            // The channel reply is the outbound transport for this run; attachment downloads are ingress.
            try
            {
                await input.SendReplyAsync(replyText, runCancellation.Token).ConfigureAwait(false);
                toolInvocationLedger.RecordCompleted(channel.ReplyAuditName, NetworkScope.Egress);
            }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                toolInvocationLedger.RecordFailed(channel.ReplyAuditName, NetworkScope.Egress);
                logger.LogError(ex, "Channel reply failed for user {UserId} on {ChannelId}.", ownerUserId, channel.ChannelId);
            }

            return string.Empty;
        }
        finally
        {
            // Dispatched requests remain auditable when execution or reply delivery fails.
            try
            {
                var auditLines = EgressAuditEventBuilder.BuildRunLines(
                    runContext,
                    egressLedger.Snapshot(),
                    toolInvocationLedger.Snapshot(),
                    EgressAuditKinds.SourceChat,
                    thread.Id,
                    null,
                    null,
                    DateTimeOffset.UtcNow);
                await egressAuditStore.AppendRangeAsync(ownerUserId, auditLines, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to append egress audit lines for {Channel} run {RunId}.", input.Channel, runId);
            }
        }
        }

        RunContext CreateRunContext(
            IReadOnlyList<AllowedDirectory> directories,
            ElicitationContext? elicitation = null) => new()
        {
            UserId = ownerUserId,
            ExecutionKind = channel.RunKind,
            ExecutionId = thread.Id,
            ProjectId = thread.ProjectId,
            IsIncognito = thread.IsIncognito,
            ChannelId = channel.ChannelId,
            Elicitation = elicitation,
            AirGapped = airGapped,
            ExecutionProviderId = shellExecutionMode,
            StandardRunDirectories = directories,
            AllowedDirectories = appService.CrewService.ResolveAllowedDirectories(
                ownerUserId,
                agentDefinition,
                directories),
            EgressLedger = egressLedger,
            UsageLedger = usageLedger,
            ToolInvocationLedger = toolInvocationLedger,
            FallbackSignal = fallbackSignal,
            CurrentAgentName = agentDefinition.Name,
            CurrentAgentId = agentDefinition.Id
        };
    }

}

/// <summary>Describes one authorized text-channel message and its transport callbacks.</summary>
public sealed record TextChannelTurnInput(
    ElicitationChannel Channel,
    string OwnerUserId,
    UserServiceScope UserScope,
    string Text,
    IReadOnlyList<ChatAttachmentInfo> RawAttachments,
    PluginElicitationOrigin Origin,
    string SenderId,
    Func<string, CancellationToken, Task<List<ChatAttachment>>> StageAttachmentsAsync,
    Func<string, CancellationToken, Task> SendReplyAsync,
    TextChannelRoute Route);

/// <summary>Supplies private execution identity and agent selection independently from the plugin transport.</summary>
public sealed record TextChannelRoute(RunExecutionKind RunKind, string HookKind, ToolApprovalChannel ApprovalChannel,
    string Title, string MetricsSource, string ReplyAuditName, string? AgentId, string ChannelId)
{
    /// <summary>Maps plugin channels to host-owned run and hook identities.</summary>
    public static TextChannelRoute ForPlugin(string channelId, string title, string? agentId)
        => new(RunExecutionKind.ChannelChat, HookExecutionKinds.ChannelChat, ToolApprovalChannel.Plugin,
            title, channelId, $"{channelId}_reply", agentId, channelId);
}
