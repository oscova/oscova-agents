using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Infrastructure.Metrics;

#pragma warning disable MAAI001 // Goal continuation uses the released experimental loop API.
namespace Oscova.Agents.Host.Server.Agents;

/// <summary>Applies chat admission and persistence boundaries around the native goal loop.</summary>
internal sealed class GoalRunLifecycle(ConversationGoalStore goals, string userId, ChatThread thread,
    ChatGoal binding, string runId, AIAgent worker, AIJudgeLoopEvaluator judge,
    ModelBudgetService budgets, Guid providerId, UsageLedger usage, ChatSteeringTarget steering,
    Func<bool> hasQueuedWork, Func<bool> hasSubagentWork, ILogger logger)
{
    private const string ContinuationAuthorName = "goal";
    public bool ApprovalResume { get; set; }
    public bool SettlingSteer { get; set; }
    public bool NeedsSteerSettlement { get; private set; }
    public IReadOnlyList<ChatMessage> HistoryBeforeCycle { get; private set; } = [];
    public IReadOnlyList<ChatMessage> CycleInputs { get; private set; } = [];
    public IReadOnlyList<ChatResponseUpdate> CycleUpdates => _cycleUpdates;
    private readonly List<ChatResponseUpdate> _cycleUpdates = [];
    private AgentResponse? _lastResponse;
    private bool _lastEvaluated;
    private bool _hasCycle;
    private AgentSession? _session;
    private string? _deferredSteering;

    /// <summary>Retains actual applied model input, including steers and completed tool exchanges.</summary>
    public void CaptureRequest(IEnumerable<ChatMessage> messages)
    {
        // The observer receives new input before the history provider prepends retained messages.
        var retained = _session is not null && _session.TryGetInMemoryChatHistory(out var history) ? history : [];
        HistoryBeforeCycle = retained.Concat(messages).Select(message => message.Clone()).ToArray();
        CycleInputs = [];
        _cycleUpdates.Clear();
    }

    /// <summary>Runs the prepared worker in the native loop and checks a final response at its cap.</summary>
    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession session, AgentRunOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        // A deferred judge can be cancelled before any new worker request captures history.
        _session = session;
        CaptureRequest([]);
        var goal = await GetActiveAsync(ct).ConfigureAwait(false);
        if (goal is null)
        {
            // Clearing pursuit still allows an approved operation in the admitted cycle to finish.
            if (ApprovalResume || SettlingSteer)
            {
                await foreach (var update in StreamWorkerAsync(messages, session, options, worker, ct).ConfigureAwait(false))
                {
                    yield return update;
                }
            }

            yield break;
        }

        if (goal.PendingResponse is { } deferred && !ApprovalResume && !SettlingSteer)
        {
            _hasCycle = true;
            _deferredSteering = goal.PendingSteering;
            _lastResponse = new AgentResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, deferred)));
            var verdict = await EvaluateAsync(new LoopContext(worker, session, [], _lastResponse, options), ct).ConfigureAwait(false);
            if (!verdict.ShouldReinvoke) { yield break; }

            if (verdict.Feedback is { } feedback)
            {
                messages = [new ChatMessage(ChatRole.User, feedback) { AuthorName = ContinuationAuthorName }];
            }

            goal = await GetActiveAsync(ct).ConfigureAwait(false);
            if (goal is null) { yield break; }
        }

        if (goal.Iterations >= goal.MaxIterations && !ApprovalResume && !SettlingSteer)
        {
            await StopAsync(ChatGoalStatus.Paused, "Iteration limit", ct).ConfigureAwait(false);
            yield break;
        }

        var observed = worker.AsBuilder().Use(null, StreamWorkerAsync).Build();
        var loop = new LoopAgent(observed, new DelegateLoopEvaluator(EvaluateAsync), new LoopAgentOptions
        {
            MaxIterations = Math.Max(1, (int)Math.Min(int.MaxValue,
                (long)goal.MaxIterations - goal.Iterations + (ApprovalResume ? 1 : 0))),
            FreshContextPerIteration = false,
            OnBehalfOfAuthorName = ContinuationAuthorName,
            ExcludeOnBehalfOfMessages = true
        });
        await foreach (var update in loop.RunStreamingAsync(messages, session, options, ct).ConfigureAwait(false))
        {
            yield return update;
        }

        if (_lastResponse is null || _lastEvaluated || HasApproval(_lastResponse)) { yield break; }
        // The native cap precedes evaluation, so the completed final cycle still receives its check.
        _ = await EvaluateAsync(new LoopContext(worker, session, [], _lastResponse, options), ct).ConfigureAwait(false);
        goal = await GetActiveAsync(ct).ConfigureAwait(false);
        if (goal is not null && goal.Iterations >= goal.MaxIterations && !NeedsSteerSettlement && !hasQueuedWork())
        {
            await StopAsync(ChatGoalStatus.Paused, "Iteration limit", ct).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<AgentResponseUpdate> StreamWorkerAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session, AgentRunOptions? options, AIAgent inner,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var goal = await GetActiveAsync(ct).ConfigureAwait(false);
        if (goal is null && !ApprovalResume && !SettlingSteer
            || hasQueuedWork() && !ApprovalResume && !SettlingSteer
            || !ApprovalResume && !await AdmitAsync(ct).ConfigureAwait(false))
        {
            NeedsSteerSettlement = false;
            yield break;
        }

        if (!ApprovalResume && !SettlingSteer)
        {
            if (goal!.Iterations >= goal.MaxIterations) { yield break; }

            var admitted = await goals.UpdateAsync(userId, thread, current =>
                IsActive(current) && current!.Iterations < current.MaxIterations
                    ? current.WithClockCheckpoint() with { Iterations = current.Iterations + 1 }
                    : current, ct).ConfigureAwait(false);
            if (!IsActive(admitted) || admitted!.Iterations != goal.Iterations + 1) { yield break; }
        }

        ApprovalResume = false;
        SettlingSteer = false;
        NeedsSteerSettlement = false;
        _session = session;
        HistoryBeforeCycle = session is not null && session.TryGetInMemoryChatHistory(out var history)
            ? history.Select(message => message.Clone()).ToArray() : [];
        CycleInputs = messages.Select(message => message.Clone()).ToArray();
        _cycleUpdates.Clear();
        _lastResponse = null;
        _lastEvaluated = false;
        _hasCycle = true;
        await foreach (var update in inner.RunStreamingAsync(CycleInputs, session, options, ct).ConfigureAwait(false))
        {
            // Capture native inputs and content before the display pipeline suppresses control messages.
            _cycleUpdates.Add(update.AsChatResponseUpdate().Clone());
            yield return update;
        }

        _lastResponse = new AgentResponse(_cycleUpdates.ToChatResponse());
        await goals.UpdateAsync(userId, thread, saved => IsActive(saved)
            ? saved!.WithClockCheckpoint() : saved, ct).ConfigureAwait(false);
    }

    private async ValueTask<LoopEvaluation> EvaluateAsync(LoopContext context, CancellationToken ct)
    {
        _lastEvaluated = true;
        if (!_hasCycle) { return LoopEvaluation.Stop(); }

        var goal = await goals.UpdateAsync(userId, thread, saved => IsActive(saved)
            ? saved!.WithClockCheckpoint() : saved, ct).ConfigureAwait(false);
        if (goal is null || !IsActive(goal)) { return LoopEvaluation.Stop(); }

        if (HasQuestion(context.LastResponse))
        {
            // Finish normally so the host can persist an answerable question card.
            await StopAsync(ChatGoalStatus.Blocked, "Waiting for your answer", ct).ConfigureAwait(false);
            return LoopEvaluation.Stop();
        }

        if (HasApproval(context.LastResponse)) { return LoopEvaluation.Stop(); }
        // Subagent results settle like steers: the parent reads them in this cycle before any judgment.
        if (steering.HasPendingMessages || hasSubagentWork())
        {
            NeedsSteerSettlement = true;
            return LoopEvaluation.Stop();
        }

        if (hasQueuedWork()) { return await DeferEvaluationAsync(context.LastResponse, ct).ConfigureAwait(false); }

        if (!await AdmitAsync(ct).ConfigureAwait(false)) { return LoopEvaluation.Stop(); }

        var accepted = steering.AcceptedCount;
        var request = new List<ChatMessage> { new(ChatRole.User, goal.Objective) };
        if (_deferredSteering is { } previousSteering) { request.Add(new ChatMessage(ChatRole.User, previousSteering)); }

        request.AddRange(steering.AppliedTexts.Select(text => new ChatMessage(ChatRole.User, text)));
        LoopEvaluation result;
        try
        {
            result = await judge.EvaluateAsync(new LoopContext(context.Agent, context.Session,
                request, context.LastResponse, context.RunOptions), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Goal completion check failed for run {RunId} on thread {ThreadId}.", runId, thread.Id);
            await StopAsync(ChatGoalStatus.Paused,
                ex is HttpRequestException ? "Provider unavailable" : "Couldn't check completion", CancellationToken.None).ConfigureAwait(false);
            return LoopEvaluation.Stop();
        }

        var current = await GetActiveAsync(ct).ConfigureAwait(false);
        if (current is null) { return LoopEvaluation.Stop(); }

        if (steering.AcceptedCount != accepted || steering.HasPendingMessages)
        {
            NeedsSteerSettlement = true;
            return LoopEvaluation.Stop();
        }

        if (hasQueuedWork()) { return await DeferEvaluationAsync(context.LastResponse, ct).ConfigureAwait(false); }

        await goals.UpdateAsync(userId, thread, saved => IsActive(saved)
            ? saved!.WithClockCheckpoint() with { PendingResponse = null, PendingSteering = null } : saved, ct).ConfigureAwait(false);
        if (current.Version != goal.Version) { return LoopEvaluation.Continue("Continue toward the updated goal objective."); }

        if (result.ShouldReinvoke) { return result; }

        if (await steering.CloseWhenIdleAsync(ct).ConfigureAwait(false))
        {
            NeedsSteerSettlement = true;
            return LoopEvaluation.Stop();
        }

        await goals.UpdateAsync(userId, thread, saved => IsActive(saved) && saved!.Version == goal.Version
            ? saved.WithStatus(ChatGoalStatus.Achieved) : saved, ct).ConfigureAwait(false);
        return LoopEvaluation.Stop();
    }

    // The native judge reads response text, so handoff retains precisely that input without another worker cycle.
    private async ValueTask<LoopEvaluation> DeferEvaluationAsync(AgentResponse response, CancellationToken ct)
    {
        await goals.UpdateAsync(userId, thread, goal => IsActive(goal)
            ? goal! with { PendingResponse = response.Text,
                PendingSteering = string.Join("\n", (_deferredSteering is null ? [] : new[] { _deferredSteering }).Concat(steering.AppliedTexts)) }
            : goal, ct).ConfigureAwait(false);
        return LoopEvaluation.Stop();
    }

    private bool IsActive(ChatGoal? goal) => goal?.Id == binding.Id
        && goal.Status == ChatGoalStatus.Active && goal.RunId == runId;

    private async Task<ChatGoal?> GetActiveAsync(CancellationToken ct)
    {
        var goal = await goals.GetAsync(userId, thread, ct).ConfigureAwait(false);
        return IsActive(goal) ? goal : null;
    }

    private async Task<bool> AdmitAsync(CancellationToken ct)
    {
        // Request-time fallback and child calls are recorded under their actual provider identities.
        foreach (var usedProvider in usage.Snapshot().Select(entry => entry.ProviderId).Append(providerId).Distinct())
        {
            var status = await budgets.GetAsync(userId, usedProvider, ct, usage).ConfigureAwait(false);
            if (!status.IsExceeded) { continue; }

            await StopAsync(ChatGoalStatus.Paused, "Budget reached", ct).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task StopAsync(ChatGoalStatus status, string reason, CancellationToken ct) =>
        _ = await goals.UpdateAsync(userId, thread,
            goal => IsActive(goal) ? goal!.WithStatus(status, reason) : goal, ct).ConfigureAwait(false);

    private static bool HasApproval(AgentResponse response) =>
        response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>().Any();

    private static bool HasQuestion(AgentResponse response) => response.Messages.SelectMany(message => message.Contents)
        .OfType<FunctionResultContent>().Any(result => result.Result?.ToString() == UserQuestions.PostedReceipt);
}
#pragma warning restore MAAI001
