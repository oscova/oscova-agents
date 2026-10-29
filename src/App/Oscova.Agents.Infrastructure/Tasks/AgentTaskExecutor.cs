using Oscova.Agents.Core.Hardware;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Hooks;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Metrics;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.ToolGovernance;
using Oscova.Agents.Conversations;
using Oscova.Agents.Definitions;
using Oscova.Agents.Hooks;
using Oscova.Agents.Llm;
using Oscova.Agents.Tasks;
using Oscova.Agents.Middleware;
using Oscova.Agents.Notifications;
using Oscova.Agents.Policies;
using Oscova.Agents.Runtime;
using Oscova.Agents.Providers;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Workspace;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Core.Nodes;

namespace Oscova.Agents.Infrastructure.Tasks
{
    public class AgentTaskExecutor(
        RunContextAccessor runContextAccessor,
        UserServiceManager userServiceManager,
        NotificationHub<TaskStatusUpdate> taskStatusNotifications,
        NotificationHub<NotificationMessage> notificationMessages,
        IServerTextLocalizer textLocalizer,
        IAgentTaskCatalog taskCatalog,
        TaskRunScopeResolver taskRunScopeResolver,
        IUserDirectoryService userDirectoryService,
        IAgentHookDispatcher hookDispatcher,
        IAgentRuntimeHookService runtimeHookService,
        IMetricsEventStore metricsEventStore,
        IEgressAuditStore egressAuditStore,
        IToolQuotaUsageStore quotaUsageStore,
        IVaultDataProtector vaultDataProtector,
        TimeProvider timeProvider,
        IHostPowerService hostPowerService,
        ILogger<AgentTaskExecutor> logger,
        ModelBudgetService budgets,
        IShellBackgroundJobProvider backgroundJobs,
        IShellExecutorProvider shellExecutors,
        INodeDispatcher nodes) : IAgentTaskExecutor
    {
        public async Task<AgentTaskRun> ExecuteAsync(
            AgentTaskDefinition task,
            AgentTaskRun? run = null,
            CancellationToken ct = default)
        {
            var agentTaskRun = run ?? new AgentTaskRun
            {
                TaskId = task.Id,
                UserId = task.UserId,
                StartedAt = timeProvider.GetUtcNow(),
                Status = AgentTaskStatus.Running
            };

            var cultureName = UiCultureCatalog.DefaultCultureName;
            var wasCancelled = false;
            var notificationPriority = NotificationPriority.Normal;
            string? skipMessageKey = null;

            if (!vaultDataProtector.IsAvailable(task.UserId))
            {
                agentTaskRun.Status = AgentTaskStatus.Skipped;
                agentTaskRun.TriggerParameters.Clear();
                try
                {
                    await PublishLockedSkipNotificationAsync(task, agentTaskRun, cultureName).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to publish the locked-key skip notification for task {TaskId}.", task.Id);
                }

                return agentTaskRun;
            }

            UserServiceScope userScope;
            AgentDefinition? agentDefinition;
            try
            {
                userScope = await userServiceManager.GetOrCreateScopeAsync(task.UserId, ct).ConfigureAwait(false);
                cultureName = userScope.UiSettings.CultureName;
                agentDefinition = await userScope.AgentDefinitionStore
                    .GetAgentAsync(task.UserId, task.AgentId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                agentTaskRun.TriggerParameters.Clear();
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                agentTaskRun.Status = AgentTaskStatus.Failed;
                agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
                agentTaskRun.Error = "The agent for this task could not be loaded.";
                logger.LogError(ex, "Failed to resolve the agent for task {TaskId} (user {UserId}).", task.Id, task.UserId);
                await PublishTaskOutcomeNotificationAsync(task, agentTaskRun, cultureName).ConfigureAwait(false);
                return agentTaskRun;
            }

            if (agentDefinition is null)
            {
                agentTaskRun.Status = AgentTaskStatus.Skipped;
                agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
                agentTaskRun.TriggerParameters.Clear();
                agentTaskRun.Error = $"Agent '{task.AgentId}' is no longer available. Reassign this task to run it.";
                logger.LogWarning(
                    "Skipped task {TaskId} for user {UserId}: backing agent {AgentId} no longer exists.",
                    task.Id,
                    task.UserId,
                    task.AgentId);
                await PublishTaskOutcomeNotificationAsync(task, agentTaskRun, cultureName).ConfigureAwait(false);
                return agentTaskRun;
            }

            IDeterministicTaskHandler? deterministicHandler = null;
            IAgentTaskHandler? agentTaskHandler = null;

            agentTaskRun.LiveMessages ??= [];

            var lastNotifiedAt = 0L; // TickCount64; used to throttle live-update notifications
            var observerAccessor = new AgentRunObserverAccessor();
            var hasLiveMessageSubscription = false;
            var egressLedger = new EgressLedger();
            var usageLedger = new UsageLedger();
            var toolInvocationLedger = new ToolInvocationLedger();
            var fallbackSignal = new FallbackActivationSignal();
            RunContext? runContext = null;
            AgentTaskContext? taskContext = null;
            AgentSession? agentSession = null;
            List<ChatMessage> historyBeforeRun = [];
            var instruction = string.Empty;

            try
            {
                // Automatic starts consume a skipped occurrence, while explicit starts remain available.
                if (agentTaskRun.TriggerSource is TaskTriggerSource.Schedule or TaskTriggerSource.AfterTask &&
                    userScope.UiSettings.IsLowBatteryTaskProtectionEnabled)
                {
                    var power = hostPowerService.GetSnapshot();
                    if (power.HasBattery is true && power.BatteryPercentage is { } percentage &&
                        percentage < userScope.UiSettings.LowBatteryTaskThresholdPercentage &&
                        power.IsExternalPowerConnected is false)
                    {
                        throw new AgentTaskSkippedException(
                            NotificationTextKeys.TaskSkippedLowBattery,
                            "This task was skipped because the computer is on battery power below your configured threshold. Run it manually when ready.");
                    }
                }

                var previousSession = task.SessionMode == TaskSessionMode.Persistent
                    ? await userScope.AgentTaskRepository.GetLatestSessionAsync(task.UserId, task.Id, ct).ConfigureAwait(false)
                    : null;
                if (previousSession is { } savedSession)
                {
                    // Keep setup failures usable as persistent-session resume points.
                    agentTaskRun.SessionElement = savedSession;
                }

                var startDecision = await DispatchTaskHookAsync(HookEventKind.TaskStart, task, agentTaskRun, isBlocking: true, ct).ConfigureAwait(false);
                if (!startDecision.IsAllowed)
                {
                    agentTaskRun.Status = AgentTaskStatus.Failed;
                    agentTaskRun.Error = startDecision.Message ?? "Task start was denied by a hook.";
                    agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
                    return agentTaskRun;
                }

                taskContext = new AgentTaskContext
                {
                    UserId = task.UserId,
                    TaskId = task.Id,
                    AgentId = task.AgentId
                };

                var validationCatalog = TaskRunScopeValidationCatalogFactory.Create(userScope, userDirectoryService);
                var runScope = SanitizeRunScope(task, validationCatalog);
                string? shellExecutionMode = userScope.ExecutionSettings.ProviderId;
                // Task runs save oversized tool output inside the task directory so it is deleted with the task.
                IReadOnlyList<AllowedDirectory> runDirectories = taskCatalog.TryGetTaskDirectory(task.Id) is { } taskDirectory
                    ? [ToolOutputStore.CreateRunDirectory(Path.Combine(taskDirectory, ToolOutputStore.FolderName, agentTaskRun.Id))]
                    : [];
                var runSurface = taskRunScopeResolver.ResolveOrThrow(
                    runScope,
                    agentDefinition,
                    effectiveAgent => userScope.CrewService.ResolveAllowedDirectories(
                        task.UserId,
                        effectiveAgent,
                        runDirectories),
                    validationCatalog);
                var effectiveAgentDefinition = runSurface.EffectiveAgent;

                // The task context is the app-owned execution identity used by
                // sandbox/container lookup and task-only tools such as notification__notify.
                // Disposal drains this run's subagents before execution cleanup.
                await using var delegatedWork = new DelegatedWorkState(ct);
                runContext = new RunContext
                {
                    UserId = task.UserId,
                    ExecutionKind = RunExecutionKind.TaskRun,
                    ExecutionId = agentTaskRun.Id,
                    Task = taskContext,
                    AirGapped = userScope.PrivacySettings.IsLocalOnly || effectiveAgentDefinition.IsLocalOnly,
                    ExecutionProviderId = shellExecutionMode,
                    StandardRunDirectories = runDirectories,
                    AllowedDirectories = runSurface.Directories,
                    ToolApprovalRunMode = task.ToolApprovalRunMode,
                    EgressLedger = egressLedger,
                    UsageLedger = usageLedger,
                    ToolInvocationLedger = toolInvocationLedger,
                    FallbackSignal = fallbackSignal,
                    CurrentAgentName = effectiveAgentDefinition.Name,
                    CurrentAgentId = effectiveAgentDefinition.Id,
                    DelegatedWork = delegatedWork,
                };
                var runContextScope = runContextAccessor.SetContext(runContext);
                await using (runContextScope.ConfigureAwait(false))
                {

                    // Seeded tasks take their handler from the live registration, so handler changes reach existing installs.
                    if (task.InstructionSource is { } seedKey)
                    {
                        if (userScope.TaskContributions.TryGetValue(seedKey, out var registration))
                        {
                            deterministicHandler = registration.Contribution.DeterministicHandlerFactory?.Invoke(registration.Context);
                            agentTaskHandler = registration.Contribution.AgentHandlerFactory?.Invoke(registration.Context);
                        }
                        else
                        {
                            // Without its plugin, a seeded task waits for the plugin in every instruction mode.
                            throw new AgentTaskSkippedException(
                                NotificationTextKeys.TaskHandlerUnavailable,
                                "This task's execution handler is unavailable. Enable or reinstall its plugin and restart the app to run it.");
                        }
                    }

                    await using var handlerLease = new TaskHandlerLease((object?)deterministicHandler ?? agentTaskHandler).ConfigureAwait(false);
                    agentTaskRun.LiveMessagesChanged += HandleLiveMessagesChanged;
                    hasLiveMessageSubscription = true;

                    if (deterministicHandler is not null)
                    {
                        await deterministicHandler.ExecuteAsync(task, ct).ConfigureAwait(false);
                        agentTaskRun.Status = AgentTaskStatus.Completed;
                        agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
                        return agentTaskRun;
                    }

                    // Model-backed tasks require a provider even when preparation finds no work.
                    ModelProviderConfig selectedProvider;
                    try
                    {
                        selectedProvider = userScope.AppService.ResolveChatModelProvider(
                            effectiveAgentDefinition,
                            task.ModelOverrideId);
                    }
                    catch (ModelProviderUnavailableException)
                    {
                        throw new AgentTaskSkippedException(
                            NotificationTextKeys.TaskMissingChatProvider,
                            "No chat model provider is configured. Set one up in Settings > Agents & AI > Model Providers to run this task.",
                            NotificationPriority.Low);
                    }

                    AgentRunPlan? agentPlan = null;
                    // Resolve before handler preparation so evidence budgeting uses the selected template.
                    if (task.InstructionSource is not null)
                    {
                        task = task.WithInstruction(InstructionSelection.Select(
                            task.InstructionMode, task.Instruction, userScope.GetTaskInstructionDefault(task)));
                    }

                    if (agentTaskHandler is not null)
                    {
                        agentPlan = await agentTaskHandler.PrepareAsync(task, ct).ConfigureAwait(false);
                        if (agentPlan is null)
                        {
                            // No work this tick. Mark completed without standing up an agent.
                            agentTaskRun.Status = AgentTaskStatus.Completed;
                            agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
                            return agentTaskRun;
                        }
                    }

                    instruction = TaskInstructionRenderer.Render(
                        agentPlan?.Instruction ?? task.Instruction,
                        new Dictionary<string, JsonElement>(agentTaskRun.TriggerParameters, StringComparer.OrdinalIgnoreCase));

                    // Task scopes are task-local runtime settings; never save the effective view back to agent.md.
                    // Task instructions are run-specific, so always build a fresh agent instead of
                    // returning a cached one that may carry a different task's instruction text.
                    userScope.CrewService.EvictAgent(effectiveAgentDefinition.Id);

                    observerAccessor.Observer = new AgentTaskRunObserver(agentTaskRun);
                    var resolution = await userScope.AppService.GetChatClientResolutionAsync(effectiveAgentDefinition, selectedProvider).ConfigureAwait(false);
                    if (!await budgets.AdmitAsync(task.UserId, resolution.EffectiveProvider.Id, ct: ct).ConfigureAwait(false))
                    {
                        throw new AgentTaskSkippedException(NotificationTextKeys.ModelBudgetTaskSkipped, "The model budget has been reached. This attempt was skipped. The task remains scheduled.");
                    }

                    // The task instruction is delivered as the user turn below, not as the system prompt, so the
                    // model has a turn to act on instead of replying with a question. Persona comes from the context provider.
                    var taskChatOptions = selectedProvider.ChatOptions.Clone();
                    if (task.MaxTokenBudget is int taskTokenBudget && taskTokenBudget > 0)
                    {
                        taskChatOptions.MaxOutputTokens = taskChatOptions.MaxOutputTokens is { } providerLimit
                            ? Math.Min(providerLimit, taskTokenBudget)
                            : taskTokenBudget;
                    }

                    var runOptions = new ChatClientAgentRunOptions(taskChatOptions);

                    IReadOnlyList<AIContextProvider> taskProviders = [];

                    var chatAgent = await userScope.AppService.GetOrCreateAgent(
                        task.UserId,
                        effectiveAgentDefinition,
                        selectedProvider.Id.ToString(),
                        configureBuilder: builder => builder.UseAgentRunObserver(observerAccessor),
                        configureOptions: options => taskProviders = options.AIContextProviders?.ToArray() ?? [],
                        taskRunSurface: runSurface,
                        configureAgentBuilder: (definition, builder) => builder.UseAgentToolHooks(
                            runtimeHookService,
                            runContextAccessor,
                            definition.Id,
                            userScope.ToolGovernanceSettings,
                            quotaUsageStore,
                            userScope.CrewService.Tools.GetNetworkScope,
                            logger)).ConfigureAwait(false);

                    agentSession = previousSession is { } serializedSession
                        ? await chatAgent.DeserializeSessionAsync(serializedSession, cancellationToken: ct).ConfigureAwait(false)
                        : await chatAgent.CreateSessionAsync(ct).ConfigureAwait(false);
                    historyBeforeRun = agentSession.TryGetInMemoryChatHistory(out var history)
                        ? history.ToList()
                        : [];

                    if (agentPlan?.FinalizeAsync is { } finalize)
                    {
                        // Use the providers built for this task, including its exact tool snapshot and directories.
                        var preview = await AgentInstructionPreviewRenderer.RenderContextAsync(taskProviders,
                            historyBeforeRun.Append(new ChatMessage(ChatRole.User, instruction)), chatAgent, agentSession, ct).ConfigureAwait(false);
                        var otherInput = ContextWindowTokenEstimator.EstimateText(preview.Instructions) +
                            ContextWindowTokenEstimator.EstimateMessages(preview.Messages) +
                            ContextWindowTokenEstimator.EstimateTools(preview.Tools);
                        var allowance = userScope.AppService.GetChatContextConfigurations(selectedProvider, resolution.EffectiveProvider)
                            .Min(provider => ContextWindowBudget.DeriveEvidenceBudget(provider.ContextWindowSize, otherInput,
                                taskChatOptions.MaxOutputTokens is { } output && provider.ChatOptions.MaxOutputTokens is { } providerOutput
                                    ? Math.Min(output, providerOutput) : taskChatOptions.MaxOutputTokens ?? provider.ChatOptions.MaxOutputTokens));
                        agentPlan = await finalize(allowance, ct).ConfigureAwait(false);
                        instruction = TaskInstructionRenderer.Render(agentPlan.Instruction,
                            new Dictionary<string, JsonElement>(agentTaskRun.TriggerParameters, StringComparer.OrdinalIgnoreCase));
                    }

                    taskContext.SerializeLiveSessionAsync = async cancellationToken =>
                        await chatAgent.SerializeSessionAsync(agentSession, cancellationToken: cancellationToken).ConfigureAwait(false);

                    var approvalContext = new ToolApprovalContext
                    {
                        UserId = task.UserId,
                        Channel = ToolApprovalChannel.BackgroundTask,
                        RunMode = task.ToolApprovalRunMode
                    };
                    var approvalRunLoop = new ToolApprovalRunLoop(userScope.ToolApprovalRouter);
                    var response = await approvalRunLoop.RunAsync(
                        chatAgent,
                        initialMessages: [new ChatMessage(ChatRole.User, instruction)],
                        agentSession,
                        runOptions,
                        approvalContext,
                        ct).ConfigureAwait(false);
                    agentTaskRun.ResultSummary = response.Text;
                    agentTaskRun.SessionElement = await chatAgent.SerializeSessionAsync(agentSession, cancellationToken: ct).ConfigureAwait(false);
                    taskContext.SessionElement = agentTaskRun.SessionElement;

                    // The framework's forced answer at the step cap is not a finished task, so success hooks do not run.
                    if (GovernedFunctionInvokingChatClient.ReachedIterationLimit(response.AdditionalProperties))
                    {
                        agentTaskRun.Status = AgentTaskStatus.Failed;
                        agentTaskRun.Error = textLocalizer.Localize(
                            userScope.UiSettings.CultureName,
                            NotificationTextKeys.TaskStepLimitReached,
                            "The task stopped at the {0}-step limit before it finished.",
                            AgentOrchestrator.MaximumToolIterations);
                        agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
                        return agentTaskRun;
                    }

                    // After-success hook for IAgentTaskHandler. If this throws, the run is marked failed
                    // by the catch below, so the watermark (or whatever the hook persists) does not advance.
                    if (agentPlan?.OnSuccessAsync is not null)
                    {
                        await agentPlan.OnSuccessAsync(ct).ConfigureAwait(false);
                    }

                    agentTaskRun.Status = AgentTaskStatus.Completed;
                    agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                wasCancelled = true;
                agentTaskRun.Status = AgentTaskStatus.Failed;
                agentTaskRun.Error = "Task was cancelled.";
                agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
            }
            catch (HookDeniedException ex)
            {
                logger.LogInformation(
                    ex,
                    "Task {TaskId} was denied by hook event {EventKind}.",
                    task.Id,
                    ex.EventKind);
                agentTaskRun.Status = AgentTaskStatus.Failed;
                agentTaskRun.Error = ex.Message;
                agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
            }
            catch (AgentTaskSkippedException ex)
            {
                notificationPriority = ex.Priority;
                skipMessageKey = ex.MessageKey;
                agentTaskRun.Status = AgentTaskStatus.Skipped;
                agentTaskRun.ResultSummary = textLocalizer.Localize(
                    userScope.UiSettings.CultureName,
                    ex.MessageKey,
                    ex.Message);
                agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
            }
            catch (Exception ex)
            {
                using (logger.BeginScope(new Dictionary<string, object?>
                {
                    ["userId"] = task.UserId,
                    ["notificationHandled"] = true
                }))
                {
                    logger.LogError(ex, "Failed to execute task {TaskId}", task.Id);
                }

                agentTaskRun.Status = AgentTaskStatus.Failed;
                agentTaskRun.Error = ex.Message;
                agentTaskRun.CompletedAt = timeProvider.GetUtcNow();
            }
            finally
            {
                if (wasCancelled)
                {
                    // A cancelled task cannot retain jobs that its deleted or finished run can no longer control.
                    try
                    {
                        try { await nodes.CleanupExecutionAsync(task.UserId, agentTaskRun.Id, CancellationToken.None).ConfigureAwait(false); }
                        finally
                        {
                            try { await backgroundJobs.CleanupExecutionAsync(agentTaskRun.Id, CancellationToken.None).ConfigureAwait(false); }
                            finally { await shellExecutors.EvictAsync(agentTaskRun.Id).ConfigureAwait(false); }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Cancelled task run {RunId} for user {UserId}, but execution cleanup failed.", agentTaskRun.Id, task.UserId);
                    }
                }

                if (agentTaskRun.Status == AgentTaskStatus.Failed &&
                    task.SessionMode == TaskSessionMode.Persistent &&
                    agentSession is not null &&
                    taskContext?.SerializeLiveSessionAsync is { } serializeLiveSessionAsync)
                {
                    // Replay the failed user turn without incomplete assistant or tool content.
                    var partialHistory = new List<ChatMessage>(historyBeforeRun)
                    {
                        new(ChatRole.User, instruction)
                    };
                    InterruptedHistory.Restore(agentSession, partialHistory);
                    try
                    {
                        agentTaskRun.SessionElement = await serializeLiveSessionAsync(CancellationToken.None).ConfigureAwait(false);
                        taskContext.SessionElement = agentTaskRun.SessionElement;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Failed to preserve the partial session for task run {RunId} on task {TaskId}.",
                            agentTaskRun.Id,
                            task.Id);
                    }
                }

                // Stop appending messages. LiveMessages itself is not cleared; it
                // stays populated so the inspector can read it after completion.
                observerAccessor.Observer = null;
                if (hasLiveMessageSubscription)
                {
                    agentTaskRun.LiveMessagesChanged -= HandleLiveMessagesChanged;
                }

                var egress = egressLedger.Snapshot();
                var usage = usageLedger.Snapshot();
                var tokenTotals = MetricsEventBuilder.SumUsage(usage);
                agentTaskRun.TokensUsed = usage.Count == 0
                    ? null
                    : tokenTotals.Total > int.MaxValue ? int.MaxValue : (int?)tokenTotals.Total;

                if (agentTaskRun.CompletedAt is not null)
                {
                    var duration = agentTaskRun.StartedAt is { } startedAt && agentTaskRun.CompletedAt is { } completedAt
                        ? (completedAt - startedAt).TotalSeconds
                        : (double?)null;
                    var tools = toolInvocationLedger.Snapshot();
                    var metricsEvent = (runContext is not null
                        ? MetricsEventBuilder.BuildEvent(
                            runContext,
                            "agent_task",
                            usage,
                            tools,
                            fallbackSignal.Activated)
                        : new MetricsEvent
                        {
                            Channel = "agent_task",
                            ExecutionKind = RunExecutionKind.TaskRun.ToString(),
                            RunId = agentTaskRun.Id,
                            TaskId = task.Id,
                            Usage = usage,
                            Tools = tools,
                            FallbackActivated = fallbackSignal.Activated
                        }) with
                    {
                        TriggerSource = agentTaskRun.TriggerSource.ToString(),
                        TaskStatus = agentTaskRun.Status.ToString(),
                        DurationSeconds = duration
                    };

                    try
                    {
                        await metricsEventStore.AppendAsync(task.UserId, metricsEvent, CancellationToken.None).ConfigureAwait(false);
                        await budgets.NotifyCompletedAsync(task.UserId, metricsEvent).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to append metrics event for task run {RunId}.", agentTaskRun.Id);
                    }

                    if (runContext is not null)
                    {
                        try
                        {
                            var auditLines = EgressAuditEventBuilder.BuildRunLines(
                                runContext,
                                egress,
                                tools,
                                EgressAuditKinds.SourceTask,
                                null,
                                agentTaskRun.Id,
                                task.Id,
                                timeProvider.GetUtcNow());
                            await egressAuditStore.AppendRangeAsync(task.UserId, auditLines, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to append egress audit lines for task run {RunId}.", agentTaskRun.Id);
                        }
                    }
                }

                if (agentTaskRun.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed)
                {
                    var eventKind = agentTaskRun.Status == AgentTaskStatus.Completed
                        ? HookEventKind.TaskComplete
                        : HookEventKind.TaskFailed;
                    try
                    {
                        await DispatchTaskHookAsync(eventKind, task, agentTaskRun, isBlocking: false, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Observe-only task hook dispatch failed for task '{TaskId}'.", task.Id);
                    }
                }

                try
                {
                    // Cancelled runs keep their history without automatic outcome alerts.
                    if (!wasCancelled)
                    {
                        await PublishTaskOutcomeNotificationAsync(task, agentTaskRun, cultureName, skipMessageKey, notificationPriority).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to publish task outcome notification for task '{TaskId}'.", task.Id);
                }
            }

            return agentTaskRun;

            async Task<HookDispatchResult> DispatchTaskHookAsync(
                HookEventKind eventKind,
                AgentTaskDefinition taskDefinition,
                AgentTaskRun run,
                bool isBlocking,
                CancellationToken cancellationToken)
            {
                var eventPayload = JsonSerializer.SerializeToElement(new
                {
                    task_name = taskDefinition.Name,
                    status = run.Status.ToString(),
                    trigger_source = run.TriggerSource.ToString()
                }, HookJsonSerializer.Options);
                var envelope = new HookEnvelope
                {
                    HookEventName = eventKind,
                    UserId = taskDefinition.UserId,
                    AgentId = taskDefinition.AgentId,
                    TaskId = taskDefinition.Id,
                    TaskRunId = run.Id,
                    ExecutionKind = HookExecutionKinds.TaskRun,
                    Event = eventPayload
                };

                return await hookDispatcher.DispatchAsync(new HookDispatchRequest
                {
                    Envelope = envelope,
                    IsBlocking = isBlocking
                }, cancellationToken).ConfigureAwait(false);
            }

            async void HandleLiveMessagesChanged()
            {
                // Throttle to at most one notification per 500 ms to avoid spamming the hub
                // during bursts of tool calls/responses.
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref lastNotifiedAt);
                if (now - last < 500)
                {
                    return;
                }

                Interlocked.CompareExchange(ref lastNotifiedAt, now, last);

                try
                {
                    var messageCount = agentTaskRun.GetLiveMessagesSnapshot()?.Length ?? 0;
                    await taskStatusNotifications.NotifyAsync(
                        task.UserId,
                        new TaskStatusUpdate(
                            task.Id,
                            task.Name,
                            agentTaskRun.Status,
                            messageCount > 0 ? $"{messageCount} live update{(messageCount == 1 ? string.Empty : "s")} available." : null)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to publish a live task update for task '{TaskId}'.", task.Id);
                }
            }
        }

        private TaskRunScope? SanitizeRunScope(
            AgentTaskDefinition task,
            TaskRunScopeValidationCatalog validationCatalog)
        {
            if (task.RunScope is null)
            {
                return null;
            }

            var scope = task.RunScope.Clone();
            var result = ToolReferenceSanitizer.RemoveUnavailableTools(
                scope.AllowedToolNames,
                scope.PinnedToolNames,
                scope.ToolPolicies,
                validationCatalog.ToolNames,
                scope.AgentToolIds,
                validationCatalog.ExposedAgentIds);

            if (result.HasChanges)
            {
                logger.LogDebug(
                    "Removed stale tool references from task {TaskId} run scope for user {UserId} before execution. Removed allowed tools: {RemovedAllowedToolCount}; removed pinned tools: {RemovedPinnedToolCount}; removed tool policies: {RemovedPolicyToolCount}; removed agent tools: {RemovedAgentToolIds}.",
                    task.Id,
                    task.UserId,
                    result.RemovedAllowedToolNames.Count,
                    result.RemovedPinnedToolNames.Count,
                    result.RemovedPolicyToolNames.Count,
                    result.RemovedAgentToolIds);
            }

            return scope;
        }

        /// <summary>Publishes stock task outcome notices directly to the app inbox.</summary>
        private async Task PublishTaskOutcomeNotificationAsync(
            AgentTaskDefinition task,
            AgentTaskRun run,
            string cultureName,
            string? skipMessageKey = null,
            NotificationPriority priority = NotificationPriority.Normal)
        {
            var shouldEmit = run.Status switch
            {
                AgentTaskStatus.Completed => task.NotifyOnCompletion,
                AgentTaskStatus.Failed => true,
                // Embeddings are optional: memory and Dreaming work without them, so only this skip stays in task history.
                AgentTaskStatus.Skipped => skipMessageKey != NotificationTextKeys.TaskMissingEmbeddingProvider,
                _ => false
            };

            if (!shouldEmit)
            {
                return;
            }

            var isSuccess = run.Status == AgentTaskStatus.Completed;
            // Inbox metadata contains stock text, with task content available through the run link.
            // A skip reason is localized from its key alone, so a handler's own message never reaches the inbox.
            var title = textLocalizer.Localize(
                cultureName,
                isSuccess ? NotificationTextKeys.TaskCompletedTitle : NotificationTextKeys.TaskNeedsAttentionTitle,
                isSuccess ? "Task completed" : "Task needs attention");
            var body = textLocalizer.Localize(
                cultureName,
                skipMessageKey ?? NotificationTextKeys.TaskOutcomeHidden,
                "Open the task to see the result.");

            await notificationMessages.NotifyAsync(task.UserId, new NotificationMessage(
                title,
                body,
                isSuccess ? NotificationKind.TaskSuccess : NotificationKind.TaskFailure,
                UserId: task.UserId,
                AgentId: task.AgentId,
                TaskId: task.Id,
                RunId: run.Id,
                Priority: priority)).ConfigureAwait(false);
        }

        /// <summary>Publishes a stock notice when a started task attempt finds its vault locked.</summary>
        private async Task PublishLockedSkipNotificationAsync(AgentTaskDefinition task, AgentTaskRun run, string cultureName)
        {
            var title = textLocalizer.Localize(
                cultureName,
                NotificationTextKeys.TaskNeedsAttentionTitle,
                "Task needs attention");
            var body = textLocalizer.Localize(
                cultureName,
                NotificationTextKeys.TaskSkippedLocked,
                "This task did not run because your Vault Password is locked. Unlock it to resume.");
            await notificationMessages.NotifyAsync(task.UserId, new NotificationMessage(
                title,
                body,
                NotificationKind.TaskFailure,
                UserId: task.UserId,
                AgentId: task.AgentId,
                TaskId: task.Id,
                RunId: run.Id)).ConfigureAwait(false);
        }

        /// <summary>Releases a contributed handler after its task run finishes.</summary>
        private readonly struct TaskHandlerLease(object? handler) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                if (handler is IAsyncDisposable asynchronous)
                {
                    await asynchronous.DisposeAsync().ConfigureAwait(false);
                }
                else if (handler is IDisposable synchronous)
                {
                    synchronous.Dispose();
                }
            }
        }
    }
}
