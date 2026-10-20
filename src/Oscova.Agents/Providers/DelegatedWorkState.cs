using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Conversations;
using Oscova.Agents.Harness;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Providers;

/// <summary>Observed lifecycle state of one subagent worker.</summary>
public enum DelegatedWorkerStatus
{
    /// <summary>The worker is running.</summary>
    Running,

    /// <summary>The worker waits for a tool approval.</summary>
    AwaitingApproval,

    /// <summary>The worker finished its task.</summary>
    Completed,

    /// <summary>The worker stopped with an error.</summary>
    Failed,

    /// <summary>The worker was stopped before finishing, or did not survive a restart.</summary>
    Interrupted
}

/// <summary>Owns the subagent workers of one root run and wakes the parent when they change.</summary>
public sealed class DelegatedWorkState : IAsyncDisposable
{
    /// <summary>Active workers allowed per root run, shared by same-agent and custom-agent targets.</summary>
    public const int MaxActiveWorkers = 4;

    /// <summary>Attribution source for worker results sent to the parent model.</summary>
    public const string ResultSourceId = "subagents";

    private static readonly AsyncLocal<DelegatedWorker?> s_currentWorker = new();

    private readonly CancellationTokenSource _cancellation;
    private readonly Lock _gate = new();
    private readonly List<DelegatedWorker> _workers = [];
    private readonly Dictionary<string, WatchedJob> _jobs = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = NewSignal();

    /// <summary>Creates the state for one root run. The root token also stops every worker.</summary>
    public DelegatedWorkState(CancellationToken rootCancellation)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(rootCancellation);
    }

    /// <summary>The worker whose execution is running on the current flow, or null for a root or consulted agent.</summary>
    public static DelegatedWorker? CurrentWorker => s_currentWorker.Value;

    /// <summary>Wakes settlement and waits so they recheck workers, jobs and pending input.</summary>
    public void Pulse()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _changed;
            _changed = NewSignal();
        }

        previous.TrySetResult();
    }

    /// <summary>Starts a worker in its own execution, or throws when no slot is free.</summary>
    public DelegatedWorker Start(
        AgentSession parentSession,
        string handle,
        string agentId,
        string? name,
        IReadOnlyList<ChatMessage> priorMessages,
        Func<ChatMessage, CancellationToken, Task> deliver,
        Func<DelegatedWorker, CancellationToken, Task<string>> execute)
    {
        DelegatedWorker worker;
        lock (_gate)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            var active = _workers.Count(item => item.IsActive);
            if (active >= MaxActiveWorkers)
            {
                throw new ToolFailureException(
                    $"All {MaxActiveWorkers} subagent slots are busy. Available: 0 of {MaxActiveWorkers}.",
                    "Wait for a running subagent to finish, then start or continue work again.");
            }

            var existing = _workers.FirstOrDefault(item => ReferenceEquals(item.ParentSession, parentSession) &&
                string.Equals(item.Handle, handle, StringComparison.Ordinal));
            if (existing is { IsActive: true })
            {
                throw new ToolFailureException($"Subagent '{handle}' is still running.", "Wait for it or interrupt it first.");
            }

            if (existing is not null)
            {
                _workers.Remove(existing);
            }

            worker = new DelegatedWorker(this, parentSession, handle, agentId, name, priorMessages, deliver,
                CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token));
            _workers.Add(worker);

            // Task.Run copies the parent's ambient context. The child run replaces the agent run context,
            // and CurrentWorker gives the copy its own execution identity, apart from the definition it runs.
            worker.Execution = Task.Run(() => RunAsync(worker, execute), CancellationToken.None);
        }

        Pulse();
        return worker;
    }

    /// <summary>Finds a worker started by the given parent session.</summary>
    public DelegatedWorker? Find(AgentSession parentSession, string handle)
    {
        lock (_gate)
        {
            return _workers.FirstOrDefault(item => ReferenceEquals(item.ParentSession, parentSession) &&
                string.Equals(item.Handle, handle, StringComparison.Ordinal));
        }
    }

    /// <summary>Lists workers started by the given parent session, oldest first.</summary>
    public IReadOnlyList<DelegatedWorker> GetWorkers(AgentSession parentSession)
    {
        lock (_gate)
        {
            return [.. _workers.Where(item => ReferenceEquals(item.ParentSession, parentSession))];
        }
    }

    /// <summary>Lists every worker of the root run, oldest first.</summary>
    public IReadOnlyList<DelegatedWorker> GetWorkers()
    {
        lock (_gate)
        {
            return [.. _workers];
        }
    }

    /// <summary>Counts active workers across the whole root run.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _workers.Count(item => item.IsActive);
            }
        }
    }

    /// <summary>True while the parent session has a running worker or a result it has not received.</summary>
    public bool HasUnsettledWork(AgentSession parentSession)
    {
        lock (_gate)
        {
            return _workers.Any(item => ReferenceEquals(item.ParentSession, parentSession) && (item.IsActive || !item.ResultTaken));
        }
    }

    /// <summary>Stops a worker and returns once its execution has exited.</summary>
    public static async Task InterruptAsync(DelegatedWorker worker)
    {
        await worker.Cancellation.CancelAsync().ConfigureAwait(false);
        // Cancel and drain before the slot is released, so a stopped worker never overlaps a new one.
        await worker.Execution.ConfigureAwait(false);
    }

    /// <summary>
    /// Lets a background job wake waits. Owner is the worker handle, or null for the parent.
    /// A job already watched keeps its first owner.
    /// </summary>
    public void Watch(string jobHandle, string? ownerWorkerHandle, Task completion)
    {
        lock (_gate)
        {
            if (!_jobs.TryAdd(jobHandle, new WatchedJob(ownerWorkerHandle, completion)))
            {
                return;
            }
        }

        _ = completion.ContinueWith(_ => Pulse(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Waits while the parent session has active workers and nothing to answer.
    /// Returns undelivered results to send, an empty list when queued input is pending while workers run, or null when settled.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>?> SettleAsync(
        AIAgent parentAgent,
        AgentSession parentSession,
        Func<CancellationToken, Task>? onWaiting,
        CancellationToken ct)
    {
        var injector = parentAgent.GetService<MessageInjectingChatClient>();
        var notified = false;
        while (true)
        {
            var changed = WhenChanged();
            bool active;
            List<ChatMessage> results = [];
            lock (_gate)
            {
                active = _workers.Any(item => item.IsActive && ReferenceEquals(item.ParentSession, parentSession));
                foreach (var worker in _workers.Where(item => !item.IsActive && ReferenceEquals(item.ParentSession, parentSession)))
                {
                    if (worker.TryTakeResult() is { } result)
                    {
                        results.Add(CreateResultMessage(result));
                    }
                }
            }

            if (results.Count > 0)
            {
                return results;
            }

            // Without workers, pending input belongs to the caller's normal steer settlement.
            if (!active)
            {
                return null;
            }

            if (injector is not null &&
                (await injector.GetPendingMessagesAsync(parentSession, ct).ConfigureAwait(false)).Count > 0)
            {
                return [];
            }

            if (!notified)
            {
                notified = true;
                if (onWaiting is not null)
                {
                    await onWaiting(ct).ConfigureAwait(false);
                }
            }

            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for selected workers or background jobs, a steer, or the timeout, and describes what it observed.
    /// With no handles it is a timed sleep that only a steer ends early.
    /// </summary>
    public async Task<string> WaitAsync(
        AIAgent agent,
        AgentSession session,
        IReadOnlyList<string> handles,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var owner = CurrentWorker?.Handle;
        var workers = new List<DelegatedWorker>();
        var jobs = new List<(string Handle, WatchedJob Job)>();
        lock (_gate)
        {
            foreach (var handle in handles)
            {
                var worker = _workers.FirstOrDefault(item => ReferenceEquals(item.ParentSession, session) &&
                    string.Equals(item.Handle, handle, StringComparison.Ordinal));
                if (worker is not null)
                {
                    workers.Add(worker);
                    continue;
                }

                if (_jobs.TryGetValue(handle, out var job) && string.Equals(job.OwnerWorkerHandle, owner, StringComparison.Ordinal))
                {
                    jobs.Add((handle, job));
                    continue;
                }

                throw new ToolFailureException($"Unknown handle '{handle}'.",
                    "Use a subagent handle or background job ID started in this conversation.");
            }
        }

        var injector = agent.GetService<MessageInjectingChatClient>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        string reason;
        while (true)
        {
            var changed = WhenChanged();
            if (workers.Find(item => !item.IsActive) is { } settled)
            {
                reason = $"Subagent '{settled.Handle}' {Describe(settled.Status)}.";
                break;
            }

            if (workers.Find(item => item.Status == DelegatedWorkerStatus.AwaitingApproval) is { } approving)
            {
                reason = $"Subagent '{approving.Handle}' needs approval.";
                break;
            }

            if (jobs.Find(item => item.Job.Completion.IsCompleted) is { Handle: { } finishedJob })
            {
                reason = $"Background job '{finishedJob}' finished.";
                break;
            }

            if (injector is not null &&
                (await injector.GetPendingMessagesAsync(session, ct).ConfigureAwait(false)).Any(message => message.IsSubmittedRequest()))
            {
                reason = "A new message arrived.";
                break;
            }

            try
            {
                await changed.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                reason = handles.Count == 0 ? $"Waited {timeout.TotalSeconds:0} seconds. Recheck the external state before continuing." : "Timed out. Selected work is still pending.";
                break;
            }
        }

        var text = new StringBuilder(reason);
        foreach (var worker in workers)
        {
            text.Append('\n').Append($"{worker.Handle}: {Describe(worker.Status)}");
            if (!worker.IsActive && worker.TryTakeResult() is { } result)
            {
                text.Append('\n').Append(result.Result);
            }
        }

        foreach (var (handle, job) in jobs)
        {
            text.Append('\n').Append($"{handle}: {(job.Completion.IsCompleted ? "finished" : "running")}");
        }

        text.Append('\n').Append($"Observed at {DateTimeOffset.UtcNow:O}.");
        return text.ToString();
    }

    /// <summary>Cancels every worker and returns once all executions have exited.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        DelegatedWorker[] workers;
        lock (_gate)
        {
            workers = [.. _workers];
        }

        foreach (var worker in workers)
        {
            await worker.Execution.ConfigureAwait(false);
            worker.Cancellation.Dispose();
        }

        _cancellation.Dispose();
    }

    /// <summary>Lower-case status words shared by tool output and waits.</summary>
    public static string Describe(DelegatedWorkerStatus status) => status switch
    {
        DelegatedWorkerStatus.AwaitingApproval => "awaiting approval",
        _ => status.ToString().ToLowerInvariant()
    };

    private async Task RunAsync(DelegatedWorker worker, Func<DelegatedWorker, CancellationToken, Task<string>> execute)
    {
        s_currentWorker.Value = worker;
        DelegatedWorkerStatus status;
        string result;
        try
        {
            result = await execute(worker, worker.Cancellation.Token).ConfigureAwait(false);
            status = DelegatedWorkerStatus.Completed;
        }
        catch (OperationCanceledException) when (worker.Cancellation.IsCancellationRequested)
        {
            status = DelegatedWorkerStatus.Interrupted;
            result = "Stopped before finishing. Partial history was kept.";
        }
        catch (Exception ex)
        {
            status = DelegatedWorkerStatus.Failed;
            result = ex.Message;
        }

        try
        {
            await worker.RunCleanupAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed cleanup still releases the slot, and the parent sees why.
            status = DelegatedWorkerStatus.Failed;
            result = $"{result}\nCleanup failed: {ex.Message}";
        }
        finally
        {
            // The result is stored before the slot is released and waiters wake, so every wake finds it.
            lock (_gate)
            {
                worker.Finish(status, result);
            }

            Pulse();
        }
    }

    private static ChatMessage CreateResultMessage(DelegatedWorkerResult result)
    {
        var message = new ChatMessage(ChatRole.User,
            $"Subagent update (not from the user): '{result.Handle}' {Describe(result.Status)}.\n{result.Result}");
        message.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            [AgentRequestMessageSourceAttribution.AdditionalPropertiesKey] =
                new AgentRequestMessageSourceAttribution(AgentRequestMessageSourceType.AIContextProvider, ResultSourceId)
        };
        return message;
    }

    private Task WhenChanged()
    {
        lock (_gate)
        {
            return _changed.Task;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record WatchedJob(string? OwnerWorkerHandle, Task Completion);
}

/// <summary>A worker's final result, taken once for delivery to its parent.</summary>
public sealed record DelegatedWorkerResult(string Handle, DelegatedWorkerStatus Status, string Result);

/// <summary>One running or finished subagent worker and its observed progress.</summary>
public sealed class DelegatedWorker
{
    private readonly DelegatedWorkState _owner;
    private readonly Lock _gate = new();
    private readonly List<AgentResponseUpdate> _updates = [];
    private readonly List<Func<Task>> _cleanup = [];
    private readonly Func<ChatMessage, CancellationToken, Task> _deliver;
    private bool _resultTaken;

    internal DelegatedWorker(
        DelegatedWorkState owner,
        AgentSession parentSession,
        string handle,
        string agentId,
        string? name,
        IReadOnlyList<ChatMessage> priorMessages,
        Func<ChatMessage, CancellationToken, Task> deliver,
        CancellationTokenSource cancellation)
    {
        _owner = owner;
        _deliver = deliver;
        ParentSession = parentSession;
        Handle = handle;
        AgentId = agentId;
        Name = name;
        PriorMessages = priorMessages;
        Cancellation = cancellation;
        ObservedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Thread-unique handle used by the lifecycle tools.</summary>
    public string Handle { get; }

    /// <summary>Definition the worker runs.</summary>
    public string AgentId { get; }

    /// <summary>Display name for the worker.</summary>
    public string? Name { get; }

    /// <summary>Session of the agent that started the worker.</summary>
    public AgentSession ParentSession { get; }

    /// <summary>History the worker started from, including its new task message.</summary>
    public IReadOnlyList<ChatMessage> PriorMessages { get; }

    /// <summary>Last observed state.</summary>
    public DelegatedWorkerStatus Status { get; private set; } = DelegatedWorkerStatus.Running;

    /// <summary>Final answer or failure text, once finished.</summary>
    public string? Result { get; private set; }

    /// <summary>When the state was last observed.</summary>
    public DateTimeOffset ObservedAt { get; private set; }

    /// <summary>True until the execution has exited and released its slot.</summary>
    public bool IsActive { get; private set; } = true;

    internal bool ResultTaken
    {
        get
        {
            lock (_gate)
            {
                return _resultTaken;
            }
        }
    }

    internal CancellationTokenSource Cancellation { get; }

    internal Task Execution { get; set; } = Task.CompletedTask;

    /// <summary>Records one streamed update and tracks whether the worker waits for approval.</summary>
    public void Record(AgentResponseUpdate update)
    {
        var changed = false;
        lock (_gate)
        {
            _updates.Add(update);
            ObservedAt = DateTimeOffset.UtcNow;
            var status = update.Contents.Any(content => content is ToolApprovalRequestContent)
                ? DelegatedWorkerStatus.AwaitingApproval
                : DelegatedWorkerStatus.Running;
            if (IsActive && status != Status)
            {
                Status = status;
                changed = true;
            }
        }

        if (changed)
        {
            _owner.Pulse();
        }
    }

    /// <summary>Returns the starting history followed by everything streamed so far.</summary>
    public IReadOnlyList<ChatMessage> GetMessages()
    {
        lock (_gate)
        {
            return [.. PriorMessages, .. _updates.ToAgentResponse().Messages];
        }
    }

    /// <summary>Queues a parent message for the worker's next model call.</summary>
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        await _deliver(new ChatMessage(ChatRole.User, text), cancellationToken).ConfigureAwait(false);
        _owner.Pulse();
    }

    /// <summary>Registers work that must finish before the worker releases its slot.</summary>
    public void OnFinished(Func<Task> cleanup)
    {
        lock (_gate)
        {
            _cleanup.Add(cleanup);
        }
    }

    /// <summary>Takes the final result once. Later calls return null.</summary>
    public DelegatedWorkerResult? TryTakeResult()
    {
        lock (_gate)
        {
            if (IsActive || _resultTaken)
            {
                return null;
            }

            _resultTaken = true;
            return new DelegatedWorkerResult(Handle, Status, Result ?? string.Empty);
        }
    }

    internal void Finish(DelegatedWorkerStatus status, string result)
    {
        lock (_gate)
        {
            Status = status;
            Result = result;
            ObservedAt = DateTimeOffset.UtcNow;
            IsActive = false;
        }
    }

    internal async Task RunCleanupAsync()
    {
        Func<Task>[] cleanup;
        lock (_gate)
        {
            cleanup = [.. _cleanup];
            _cleanup.Clear();
        }

        foreach (var item in cleanup)
        {
            await item().ConfigureAwait(false);
        }
    }
}
