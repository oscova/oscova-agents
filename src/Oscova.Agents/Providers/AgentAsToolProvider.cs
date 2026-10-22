using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Conversations;
using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Providers;

/// <summary>
/// Provides agent-as-tool functions and the subagent lifecycle tools for one parent agent.
/// </summary>
public class AgentAsToolProvider : AIContextProvider
{
    /// <summary>Starts a same-agent subagent.</summary>
    public const string StartToolName = "start_subagent";

    /// <summary>Reads subagent state and recent output.</summary>
    public const string StatusToolName = "subagent_status";

    /// <summary>Sends a message to a running subagent.</summary>
    public const string MessageToolName = "message_subagent";

    /// <summary>Stops a running subagent.</summary>
    public const string InterruptToolName = "interrupt_subagent";

    /// <summary>Continues a retained subagent history.</summary>
    public const string ContinueToolName = "continue_subagent";

    private const int MaxNameLength = 40;
    private const int MaxMessageLength = 1000;

    // Workers store snapshots concurrently with the parent, so state is replaced, never mutated in place.
    private static readonly Lock s_stateGate = new();

    private readonly IReadOnlyList<ChatClientAgent> _agents;
    private readonly Func<DelegatedWorkState?> _resolveWork;
    private readonly string? _selfAgentId;
    private readonly Func<CancellationToken, Task<ChatClientAgent>>? _createSelfWorker;
    private readonly ProviderSessionState<State> _sessionState = new(
        static _ => new State(),
        nameof(AgentAsToolProvider));

    /// <summary>
    /// Creates the provider. Without a work state, agent calls run synchronously.
    /// A self-worker factory enables same-agent subagents for the agent with <paramref name="selfAgentId"/>.
    /// </summary>
    public AgentAsToolProvider(
        IEnumerable<string> agentToolIds,
        AIAgentCollection agents,
        Func<DelegatedWorkState?>? resolveWork = null,
        string? selfAgentId = null,
        Func<CancellationToken, Task<ChatClientAgent>>? createSelfWorker = null)
    {
        _agents =
        [
            .. agentToolIds
                .Select(agentId => agents.TryGetAgentById(agentId, out var agent) ? agent : null)
                .OfType<ChatClientAgent>()
        ];
        _resolveWork = resolveWork ?? (static () => null);
        _selfAgentId = selfAgentId;
        _createSelfWorker = createSelfWorker;
    }

    /// <summary>Default guidance for same-agent subagents.</summary>
    public static string DefaultInstructions { get; } =
        $"""
        Use subagents for independent work that can run while you continue.

        - Call {StartToolName} with a self-contained task. A subagent is a separate instance of you with a fresh history.
        - Finish quick steps yourself instead of starting a subagent.
        - Up to {DelegatedWorkState.MaxActiveWorkers} subagents can run at once.
        - Subagents cannot start subagents or ask the user questions.
        - When you have nothing else to do, call {WaitToolset.QualifiedToolName} with the subagent handles.
        - Each subagent result is delivered to you before you finish. Check it before you answer.
        """;

    /// <summary>Guidance added when same-agent subagents are offered.</summary>
    public string? Instructions { get; init; } = DefaultInstructions;

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        // A worker shares its definition with the parent but never delegates further.
        if (DelegatedWorkState.CurrentWorker is not null)
        {
            return new ValueTask<AIContext>(new AIContext());
        }

        var tools = new List<AITool>(_agents.Count + 5);
        foreach (var agent in _agents)
        {
            tools.Add(CreateAgentFunction(agent, context.Session));
        }

        string? instructions = null;
        if (context.Session is { } parentSession && _resolveWork() is not null)
        {
            if (_createSelfWorker is not null)
            {
                tools.Add(CreateStartFunction(parentSession));
                instructions = Instructions;
            }

            tools.AddRange(CreateLifecycleFunctions(context.Agent, parentSession));
        }

        return new ValueTask<AIContext>(new AIContext { Tools = tools, Instructions = instructions });
    }

    /// <summary>Reads the delegated conversations of a parent session, using live progress for running workers.</summary>
    public async ValueTask<IReadOnlyList<AgentToolConversation>> GetConversationsAsync(
        AIAgent sessionOwner,
        AgentSession parentSession,
        DelegatedWorkState? live = null,
        CancellationToken cancellationToken = default)
    {
        var entries = ReadState(parentSession).Conversations;
        var conversations = new List<AgentToolConversation>(entries.Count);
        foreach (var entry in entries)
        {
            var worker = live?.Find(parentSession, entry.Handle);
            var messages = worker is { IsActive: true }
                ? worker.GetMessages()
                : await ReadMessagesAsync(sessionOwner, entry, cancellationToken).ConfigureAwait(false);
            conversations.Add(new AgentToolConversation(entry.AgentId, entry.Name, entry.Handle,
                ResolveStatus(entry, worker), messages));
        }

        // A worker that just started has not stored its first snapshot yet.
        foreach (var worker in live?.GetWorkers(parentSession) ?? [])
        {
            if (entries.All(entry => entry.Handle != worker.Handle))
            {
                conversations.Add(new AgentToolConversation(worker.AgentId, worker.Name, worker.Handle,
                    worker.Status, worker.GetMessages()));
            }
        }

        return conversations;
    }

    /// <summary>Creates the callable wrapper while letting a harness add governed conversation destinations.</summary>
    protected virtual AIFunction CreateAgentFunction(ChatClientAgent agent, AgentSession? parentSession)
    {
        [Description("Invoke an agent to retrieve some information.")]
        async Task<string> InvokeAgentAsync(
            [Description("Input query to invoke the agent.")] string query,
            [Description("Optional name for a separate line of work with this agent. Reuse the same name to continue that conversation, or pick a new name to start a separate one.")] string? conversation = null,
            CancellationToken cancellationToken = default)
        {
            RequireParent();
            var runOptions = CurrentRunOptions();
            if (parentSession is null)
            {
                var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                var stateless = await RunAgentAsync(agent,
                    [new ChatMessage(ChatRole.User, query)], session, runOptions, null, cancellationToken)
                    .ConfigureAwait(false);
                return stateless.Text;
            }

            var name = string.IsNullOrWhiteSpace(conversation) ? null : conversation.Trim();
            var entry = ReadState(parentSession).Find(agent.Id, name);
            // Names match case-insensitively, so a resumed conversation keeps its first spelling.
            name = entry?.Name ?? name;
            if (_resolveWork() is { } work)
            {
                // A worker stores its first snapshot only once it runs, so a repeated call finds it live and reports it running.
                var live = entry is null
                    ? work.GetWorkers(parentSession).FirstOrDefault(item => item.AgentId == agent.Id &&
                        string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                    : null;
                return await StartWorkerAsync(work, parentSession, agent, entry,
                    entry?.Handle ?? live?.Handle ?? NewHandle(name ?? agent.Name ?? agent.Id), live?.Name ?? name, query, cancellationToken)
                    .ConfigureAwait(false);
            }

            var childSession = entry is null
                ? await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false)
                : await agent.DeserializeSessionAsync(entry.Session, cancellationToken: cancellationToken).ConfigureAwait(false);
            var response = await RunAgentAsync(agent,
                [new ChatMessage(ChatRole.User, query)], childSession, runOptions, null, cancellationToken)
                .ConfigureAwait(false);

            // Storing after the run keeps a failed or cancelled call from replacing the last good snapshot.
            await StoreAsync(parentSession, agent, childSession, entry?.Handle ?? NewHandle(name ?? agent.Name ?? agent.Id),
                name, DelegatedWorkerStatus.Completed, cancellationToken).ConfigureAwait(false);
            return response.Text;
        }

        return AIFunctionFactory.Create(InvokeAgentAsync, new AIFunctionFactoryOptions
        {
            Name = agent.Name is null ? null : AgentCallableName.Normalize(agent.Name),
            Description = agent.Description
        });
    }

    /// <summary>Executes a child while preserving this provider's named session ownership.</summary>
    protected virtual async Task<AgentResponse> RunAgentAsync(
        ChatClientAgent agent,
        IEnumerable<ChatMessage> messages,
        AgentSession session,
        AgentRunOptions? options,
        Action<AgentResponseUpdate>? onUpdate,
        CancellationToken cancellationToken)
    {
        if (onUpdate is null)
        {
            return await agent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        }

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            onUpdate(update);
        }

        return updates.ToAgentResponse();
    }

    private AIFunction CreateStartFunction(AgentSession parentSession)
    {
        async Task<string> StartAsync(
            [Description("Everything the subagent needs to finish the task.\nIt cannot see this conversation.")] string task,
            [Description("Short readable task name, 1 to 40 characters.")] string name,
            CancellationToken cancellationToken = default)
        {
            RequireParent();
            var work = RequireWork();
            if (string.IsNullOrWhiteSpace(task))
            {
                throw new ToolFailureException("task is empty.", "Describe the work the subagent must do.");
            }

            name = name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > MaxNameLength)
            {
                throw new ToolFailureException($"name must be 1 to {MaxNameLength} characters.", "Use a short task name.");
            }

            return await StartWorkerAsync(work, parentSession, null, null, NewHandle(name), name, task, cancellationToken)
                .ConfigureAwait(false);
        }

        return AIFunctionFactory.Create(StartAsync, new AIFunctionFactoryOptions
        {
            Name = StartToolName,
            Description =
                "Starts a subagent: a separate instance of you with your tools and a fresh history.\n" +
                "Returns its handle at once while it works in the background.\n" +
                $"At most {DelegatedWorkState.MaxActiveWorkers} subagents run at once.\n" +
                "Subagents cannot start subagents or ask the user questions.\n" +
                "Its result is delivered to you before you finish.\n" +
                $"To wait for it, call {WaitToolset.QualifiedToolName} with its handle."
        });
    }

    private IEnumerable<AIFunction> CreateLifecycleFunctions(AIAgent parentAgent, AgentSession parentSession)
    {
        async Task<string> StatusAsync(
            [Description("Subagent handle. Omit to list every subagent in this conversation.")] string? handle = null,
            [Description("Recent messages to show for one subagent, 1 to 20. Default 5.")] int recentMessages = 5,
            CancellationToken cancellationToken = default)
        {
            RequireParent();
            var work = _resolveWork();
            var conversations = await GetConversationsAsync(parentAgent, parentSession, work, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(handle))
            {
                return conversations.Count == 0
                    ? "No subagents in this conversation."
                    : string.Join('\n', conversations.Select(StatusLine));
            }

            var conversation = conversations.FirstOrDefault(item => item.Handle == handle.Trim())
                ?? throw UnknownHandle(handle);
            var worker = work?.Find(parentSession, conversation.Handle);
            return Describe(conversation, worker, Math.Clamp(recentMessages, 1, 20));
        }

        async Task<string> MessageAsync(
            [Description("Handle of a running subagent from this conversation.")] string handle,
            [Description("Message for the subagent.")] string message,
            CancellationToken cancellationToken = default)
        {
            RequireParent();
            var worker = RequireWork().Find(parentSession, handle.Trim()) ?? throw UnknownHandle(handle);
            if (!worker.IsActive)
            {
                throw new ToolFailureException($"Subagent '{worker.Handle}' is {DelegatedWorkState.Describe(worker.Status)}.",
                    $"Use {ContinueToolName} to give it more work.");
            }

            await worker.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return $"Sent to '{worker.Handle}'. It reads the message before its next model call.";
        }

        async Task<string> InterruptAsync(
            [Description("Handle of a running subagent from this conversation.")] string handle,
            CancellationToken cancellationToken = default)
        {
            RequireParent();
            var work = RequireWork();
            var worker = work.Find(parentSession, handle.Trim()) ?? throw UnknownHandle(handle);
            if (worker.IsActive)
            {
                await DelegatedWorkState.InterruptAsync(worker).ConfigureAwait(false);
            }

            // A worker that finished before the stop reports its real result.
            var result = worker.TryTakeResult();
            return $"Subagent '{worker.Handle}' {DelegatedWorkState.Describe(worker.Status)}." +
                (result is null ? string.Empty : $"\n{result.Result}");
        }

        async Task<string> ContinueAsync(
            [Description("Handle of a finished or interrupted subagent from this conversation.")] string handle,
            [Description("New instructions or context for the subagent.")] string message,
            CancellationToken cancellationToken = default)
        {
            RequireParent();
            var work = RequireWork();
            var entry = ReadState(parentSession).Conversations.FirstOrDefault(item => item.Handle == handle.Trim())
                ?? throw UnknownHandle(handle);
            if (work.Find(parentSession, entry.Handle) is { IsActive: true })
            {
                throw new ToolFailureException($"Subagent '{entry.Handle}' is still running.",
                    $"Use {MessageToolName} or wait for it to finish.");
            }

            ChatClientAgent? target = null;
            if (entry.AgentId != _selfAgentId)
            {
                target = _agents.FirstOrDefault(agent => agent.Id == entry.AgentId)
                    ?? throw new ToolFailureException($"The agent behind '{entry.Handle}' is no longer available.");
            }
            else if (_createSelfWorker is null)
            {
                throw new ToolFailureException("Subagents are turned off for this agent.");
            }

            return await StartWorkerAsync(work, parentSession, target, entry, entry.Handle, entry.Name, message, cancellationToken)
                .ConfigureAwait(false);
        }

        yield return AIFunctionFactory.Create(StatusAsync, new AIFunctionFactoryOptions
        {
            Name = StatusToolName,
            Description =
                "Reads subagent state, current tool, latest progress, recent messages and result.\n" +
                $"Each message is cut to {MaxMessageLength} characters.\n" +
                "Reading never stops or slows a subagent."
        });
        yield return AIFunctionFactory.Create(MessageAsync, new AIFunctionFactoryOptions
        {
            Name = MessageToolName,
            Description =
                "Sends a message to a running subagent, such as new context or a progress request.\n" +
                "The subagent reads it before its next model call."
        });
        yield return AIFunctionFactory.Create(InterruptAsync, new AIFunctionFactoryOptions
        {
            Name = InterruptToolName,
            Description =
                "Stops a running subagent and keeps its partial history.\n" +
                "Returns the final state, which is completed if it finished first.\n" +
                $"Use {ContinueToolName} to resume it later."
        });
        yield return AIFunctionFactory.Create(ContinueAsync, new AIFunctionFactoryOptions
        {
            Name = ContinueToolName,
            Description =
                "Continues a finished or interrupted subagent with its retained history.\n" +
                "Returns at once while it works in the background.\n" +
                $"Uses one of the {DelegatedWorkState.MaxActiveWorkers} shared subagent slots."
        });
    }

    private async Task<string> StartWorkerAsync(
        DelegatedWorkState work,
        AgentSession parentSession,
        ChatClientAgent? target,
        Entry? prior,
        string handle,
        string? name,
        string task,
        CancellationToken cancellationToken)
    {
        var agent = target ?? await _createSelfWorker!(cancellationToken).ConfigureAwait(false);
        var childSession = prior is null
            ? await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false)
            : await agent.DeserializeSessionAsync(prior.Session, cancellationToken: cancellationToken).ConfigureAwait(false);
        List<ChatMessage> history = childSession.TryGetInMemoryChatHistory(out var messages) ? [.. messages] : [];
        var input = new ChatMessage(ChatRole.User, task);
        var injector = agent.GetService<MessageInjectingChatClient>();
        var runOptions = CurrentRunOptions();
        var delivered = new ConcurrentQueue<ChatMessage>();
        // The final pending check closes input under this gate, so a later message is refused, never lost.
        var inputGate = new SemaphoreSlim(1, 1);
        var inputClosed = false;

        async Task DeliverAsync(ChatMessage message, CancellationToken ct)
        {
            if (injector is null)
            {
                throw new ToolFailureException("This subagent cannot receive messages while it runs.");
            }

            await inputGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (inputClosed)
                {
                    throw new ToolFailureException($"Subagent '{handle}' has finished.", $"Use {ContinueToolName} to give it more work.");
                }

                await injector.EnqueueMessagesAsync(childSession, [message], ct).ConfigureAwait(false);
                delivered.Enqueue(message);
            }
            finally
            {
                inputGate.Release();
            }
        }

        async Task<bool> CloseInputWhenIdleAsync(CancellationToken ct)
        {
            await inputGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                inputClosed = injector is null || (await injector.GetPendingMessagesAsync(childSession, ct).ConfigureAwait(false)).Count == 0;
                return inputClosed;
            }
            finally
            {
                inputGate.Release();
            }
        }

        async Task<string> ExecuteAsync(DelegatedWorker worker, CancellationToken ct)
        {
            await StoreAsync(parentSession, agent, childSession, handle, name, DelegatedWorkerStatus.Running, ct)
                .ConfigureAwait(false);
            var updates = new List<ChatResponseUpdate>();
            void OnUpdate(AgentResponseUpdate update)
            {
                worker.Record(update);
                if (!update.Contents.Any(content => content is ToolApprovalRequestContent or ToolApprovalResponseContent))
                {
                    updates.Add(update.AsChatResponseUpdate());
                }
            }

            try
            {
                IEnumerable<ChatMessage> next = [input];
                while (true)
                {
                    var response = await RunAgentAsync(agent, next, childSession, runOptions, OnUpdate, ct).ConfigureAwait(false);
                    // A message sent during the final model call still reaches the worker.
                    if (await CloseInputWhenIdleAsync(ct).ConfigureAwait(false))
                    {
                        await StoreAsync(parentSession, agent, childSession, handle, name, DelegatedWorkerStatus.Completed, ct)
                            .ConfigureAwait(false);
                        return response.Text;
                    }

                    next = [];
                }
            }
            catch (Exception ex)
            {
                // Keep finished text and complete tool pairs so a later continue resumes from real progress.
                // Consumed parent messages never stream back as updates, so they are appended after that progress.
                await inputGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                inputClosed = true;
                inputGate.Release();
                var pending = injector is null ? [] : await injector.GetPendingMessagesAsync(childSession, CancellationToken.None).ConfigureAwait(false);
                InterruptedHistory.Restore(childSession,
                    [.. InterruptedHistory.Build(history, [input], updates), .. delivered.Where(message => !pending.Contains(message))]);
                var status = ct.IsCancellationRequested && ex is OperationCanceledException
                    ? DelegatedWorkerStatus.Interrupted
                    : DelegatedWorkerStatus.Failed;
                await StoreAsync(parentSession, agent, childSession, handle, name, status, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
        }

        var previous = work.Find(parentSession, handle);
        var started = work.Start(parentSession, handle, agent.Id, name, [.. history, input], DeliverAsync, ExecuteAsync);
        // Start drops the finished invocation, so a result the parent never received travels with this receipt.
        var undelivered = previous?.TryTakeResult();
        return $"Started subagent '{started.Handle}'" + (name is null ? "." : $" named '{name}'.") +
            "\nIts result is delivered to you before you finish." +
            $"\nTo wait for it, call {WaitToolset.QualifiedToolName} with this handle." +
            (undelivered is null ? string.Empty
                : $"\nPrevious result, {DelegatedWorkState.Describe(undelivered.Status)}:\n{undelivered.Result}");
    }

    private async Task StoreAsync(
        AgentSession parentSession,
        AIAgent agent,
        AgentSession childSession,
        string handle,
        string? name,
        DelegatedWorkerStatus status,
        CancellationToken cancellationToken)
    {
        var serialized = await agent.SerializeSessionAsync(childSession, cancellationToken: cancellationToken).ConfigureAwait(false);
        var entry = new Entry { AgentId = agent.Id, Name = name, Handle = handle, Status = status, Session = serialized };
        lock (s_stateGate)
        {
            var current = ReadState(parentSession).Conversations;
            var index = current.ToList().FindIndex(item => item.Handle == handle);
            List<Entry> next = [.. current];
            if (index < 0)
            {
                next.Add(entry);
            }
            else
            {
                next[index] = entry;
            }

            _sessionState.SaveState(parentSession, new State { Conversations = next });
        }
    }

    private State ReadState(AgentSession parentSession)
    {
        lock (s_stateGate)
        {
            return _sessionState.GetOrInitializeState(parentSession);
        }
    }

    private static async Task<IReadOnlyList<ChatMessage>> ReadMessagesAsync(
        AIAgent sessionOwner,
        Entry entry,
        CancellationToken cancellationToken)
    {
        var session = await sessionOwner
            .DeserializeSessionAsync(entry.Session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return session.TryGetInMemoryChatHistory(out var history) ? history : [];
    }

    // A stored running entry without a live worker was cut off by a host restart or Stop.
    private static DelegatedWorkerStatus ResolveStatus(Entry entry, DelegatedWorker? worker) =>
        worker?.Status ?? (entry.Status == DelegatedWorkerStatus.Running ? DelegatedWorkerStatus.Interrupted : entry.Status);

    // The handle is the only identity the model can pass back, so an unnamed entry shows no agent id.
    private static string StatusLine(AgentToolConversation conversation) =>
        (conversation.Name is null ? conversation.Handle : $"{conversation.Handle} ({conversation.Name})")
        + $": {DelegatedWorkState.Describe(conversation.Status)}";

    private static string Describe(AgentToolConversation conversation, DelegatedWorker? worker, int recentMessages)
    {
        var messages = conversation.Messages.Where(message => message.Role != ChatRole.System).ToList();
        var results = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
        var currentTool = messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .LastOrDefault(call => !results.Contains(call.CallId));
        var progress = messages.LastOrDefault(message => message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text));

        var text = new StringBuilder(StatusLine(conversation));
        if (conversation.Status is DelegatedWorkerStatus.Running or DelegatedWorkerStatus.AwaitingApproval)
        {
            text.Append('\n').Append(currentTool is null ? "Current tool: none" : $"Current tool: {currentTool.Name}");
        }

        if (progress is not null)
        {
            text.Append("\nLatest progress: ").Append(Truncate(progress.Text));
        }

        text.Append("\nRecent messages:");
        foreach (var message in messages.TakeLast(recentMessages))
        {
            text.Append('\n').Append(message.Role.Value).Append(": ").Append(Truncate(Summarize(message)));
        }

        if (worker?.TryTakeResult() is { } result)
        {
            text.Append("\nResult: ").Append(result.Result);
        }

        text.Append($"\nObserved at {(worker?.ObservedAt ?? DateTimeOffset.UtcNow):O}.");
        return text.ToString();
    }

    private static string Summarize(ChatMessage message) => string.Join(' ', message.Contents.Select(content => content switch
    {
        TextContent text => text.Text,
        FunctionCallContent call => $"[calls {call.Name}]",
        FunctionResultContent result => $"[tool result] {result.Result}",
        _ => string.Empty
    }).Where(part => part.Length > 0));

    private static string Truncate(string text) =>
        text.Length <= MaxMessageLength ? text : string.Concat(text.AsSpan(0, MaxMessageLength), "...");

    private static string NewHandle(string name)
    {
        var slug = AgentCallableName.Normalize(name).Trim('_').Replace('_', '-').ToLowerInvariant();
        slug = slug.Length == 0 ? "subagent" : slug[..Math.Min(slug.Length, 24)].TrimEnd('-');
        return $"{slug}-{Guid.NewGuid().ToString("N")[..4]}";
    }

    private static AgentRunOptions? CurrentRunOptions() =>
        FunctionInvokingChatClient.CurrentContext?.Options?.AdditionalProperties is { } properties
            ? new AgentRunOptions { AdditionalProperties = properties }
            : null;

    /// <summary>Rejects delegation from inside a subagent worker.</summary>
    protected static void RequireParent()
    {
        if (DelegatedWorkState.CurrentWorker is not null)
        {
            throw new ToolFailureException("Subagents cannot delegate work.", "Finish the assigned task yourself.");
        }
    }

    private DelegatedWorkState RequireWork() => _resolveWork()
        ?? throw new ToolFailureException("Subagents are unavailable in this run.");

    private static ToolFailureException UnknownHandle(string handle) =>
        new($"Unknown subagent handle '{handle}'.", $"Call {StatusToolName} without a handle to list subagents.");

    /// <summary>Retained child sessions of one parent session.</summary>
    private sealed class State
    {
        public IReadOnlyList<Entry> Conversations { get; init; } = [];

        public Entry? Find(string agentId, string? name) =>
            Conversations.FirstOrDefault(entry =>
                string.Equals(entry.AgentId, agentId, StringComparison.Ordinal) &&
                string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One retained child session and its last stored status.</summary>
    private sealed class Entry
    {
        public string AgentId { get; init; } = string.Empty;
        public string? Name { get; init; }
        public string Handle { get; init; } = string.Empty;
        public DelegatedWorkerStatus Status { get; init; }
        public JsonElement Session { get; init; }
    }
}
