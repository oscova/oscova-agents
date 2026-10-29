using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Hooks;
using Oscova.Agents.Providers;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Routes follow-up text from any channel into the live agent run of a chat thread.</summary>
public sealed class ChatSteeringRegistry(
    IAgentRuntimeHookService runtimeHookService,
    RunContextAccessor runContextAccessor)
{
    private readonly ConcurrentDictionary<(string UserId, string ThreadId), ChatSteeringTarget> _targets = new(UserThreadComparer.Instance);

    /// <summary>Makes a starting run the steering target for its thread until the target is disposed.</summary>
    public ChatSteeringTarget Register(
        string userId,
        string threadId,
        string agentId,
        AIAgent agent,
        AgentSession session,
        string runId,
        RunContext runContext,
        Func<ChatDisplayMessage, CancellationToken, Task> onApplied,
        Func<ChatDisplayMessage, CancellationToken, Task<bool>>? onReplace = null)
    {
        var target = new ChatSteeringTarget(this, userId, threadId, agentId, agent, session, runId, runContext, onApplied, onReplace);
        _targets[(userId, threadId)] = target;
        return target;
    }

    /// <summary>Queues text into the live run and returns its ID, or null when no run can take it.</summary>
    public async Task<string?> TrySteerAsync(
        string userId,
        string threadId,
        ChatDisplayMessage message,
        string executionKind,
        string? channelId,
        CancellationToken ct)
    {
        if (!_targets.TryGetValue((userId, threadId), out var target))
        {
            return null;
        }

        // A message to a different participant follows the caller's existing queue fallback.
        if (message.RecipientAgentId is not null && message.RecipientAgentId != target.AgentId) { return null; }

        // Follow-up requests inherit the accepting run's offline policy and identity.
        await using var hookScope = runContextAccessor.SetContext(target.RunContext);
        var decision = await runtimeHookService.DispatchUserPromptSubmitAsync(new PromptHookContext
        {
            UserId = userId,
            AgentId = target.AgentId,
            ChatThreadId = threadId,
            ExecutionKind = executionKind,
            ChannelId = channelId,
            PromptText = ChatDisplayMessageFormatter.ComposeFiles(message.Text, message.WorkspaceFiles)
        }, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw new HookDeniedException(
                HookEventKind.UserPromptSubmit,
                toolName: null,
                decision.Message ?? "User prompt was denied by a hook.",
                decision.Reason);
        }

        return await target.TryEnqueueAsync(message, ct).ConfigureAwait(false) ? target.RunId : null;
    }

    /// <summary>Returns the live run of a thread, or null when none is running.</summary>
    public ChatSteeringTarget? Find(string userId, string threadId) =>
        _targets.TryGetValue((userId, threadId), out var target) ? target : null;

    internal void Unregister(ChatSteeringTarget target) =>
        _targets.TryRemove(KeyValuePair.Create((target.UserId, target.ThreadId), target));

    /// <summary>Preserves exact user identity and case-insensitive thread identity for live routing.</summary>
    private sealed class UserThreadComparer : IEqualityComparer<(string UserId, string ThreadId)>
    {
        public static UserThreadComparer Instance { get; } = new();

        public bool Equals((string UserId, string ThreadId) left, (string UserId, string ThreadId) right) =>
            string.Equals(left.UserId, right.UserId, StringComparison.Ordinal) &&
            string.Equals(left.ThreadId, right.ThreadId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string UserId, string ThreadId) key) => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(key.UserId), StringComparer.OrdinalIgnoreCase.GetHashCode(key.ThreadId));
    }
}

/// <summary>Holds one live run's pending steers and reports each one when the model receives it.</summary>
public sealed class ChatSteeringTarget : IDisposable
{
    // MAF keeps unconsumed injected messages in the session state bag under this key.
    private const string PendingMessagesStateKey = "MessageInjectingChatClient.PendingInjectedMessages";

    private readonly ChatSteeringRegistry _owner;
    private readonly Func<ChatDisplayMessage, CancellationToken, Task> _onApplied;
    private readonly Func<ChatDisplayMessage, CancellationToken, Task<bool>>? _onReplace;
    private readonly MessageInjectingChatClient _injector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ChatDisplayMessage> _pending = new(StringComparer.Ordinal);
    private readonly List<string> _appliedTexts = [];
    private readonly HashSet<string> _acceptedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _withdrawnIds = new(StringComparer.Ordinal);
    private bool _closed;

    internal ChatSteeringTarget(
        ChatSteeringRegistry owner,
        string userId,
        string threadId,
        string agentId,
        AIAgent agent,
        AgentSession session,
        string runId,
        RunContext runContext,
        Func<ChatDisplayMessage, CancellationToken, Task> onApplied,
        Func<ChatDisplayMessage, CancellationToken, Task<bool>>? onReplace)
    {
        _owner = owner;
        _onReplace = onReplace;
        UserId = userId;
        ThreadId = threadId;
        AgentId = agentId;
        RunId = runId;
        RunContext = runContext;
        Session = session;
        Agent = agent;
        _onApplied = onApplied;
        _injector = agent.GetService<MessageInjectingChatClient>()
            ?? throw new InvalidOperationException("The agent pipeline has no message injector.");
    }

    public string UserId { get; }

    public string ThreadId { get; }

    public string AgentId { get; }

    public string RunId { get; }

    /// <summary>The root agent of the live run.</summary>
    public AIAgent Agent { get; }

    /// <summary>The root session of the live run.</summary>
    public AgentSession Session { get; }

    internal RunContext RunContext { get; }

    /// <summary>Subagent workers of the live run, or null when the run cannot delegate.</summary>
    public DelegatedWorkState? DelegatedWork => RunContext.DelegatedWork;

    /// <summary>Texts the model has received, in the order it received them.</summary>
    public IReadOnlyList<string> AppliedTexts
    {
        get
        {
            lock (_pending)
            {
                return [.. _appliedTexts];
            }
        }
    }

    private int _acceptedCount;
    /// <summary>Identifies steers accepted during a completion check.</summary>
    public int AcceptedCount => Volatile.Read(ref _acceptedCount);

    /// <summary>Reports accepted steers that the worker has not received.</summary>
    public bool HasPendingMessages { get { lock (_pending) { return _pending.Count > 0; } } }

    internal async Task<bool> TryEnqueueAsync(ChatDisplayMessage message, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A stopped run may still be unwinding tools before it closes, and would discard the input.
            if (_closed || RunContext.ConfigurationScope?.Token.IsCancellationRequested == true ||
                _withdrawnIds.Contains(message.MessageId))
            {
                return false;
            }

            // A repeated message, such as a retried answer, is already this run's input.
            if (_acceptedIds.Contains(message.MessageId))
            {
                return true;
            }

            lock (_pending)
            {
                _pending[message.MessageId] = message;
            }

            try
            {
                await _injector.EnqueueMessagesAsync(
                    Session,
                    [new ChatMessage(ChatRole.User, ChatDisplayMessageFormatter.ComposeFiles(message.Text, message.WorkspaceFiles)) { MessageId = message.MessageId }],
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // An answer that never reached the queue stays retryable.
                lock (_pending)
                {
                    _pending.Remove(message.MessageId);
                }

                throw;
            }

            _acceptedIds.Add(message.MessageId);
            Interlocked.Increment(ref _acceptedCount);
            // A steer ends the parent's wait for subagents so it can answer.
            RunContext.DelegatedWork?.Pulse();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces a saved transcript message through the live run, or returns false when the run no longer takes changes.</summary>
    /// <param name="message">The saved message to replace.</param>
    /// <param name="withdrawnMessageId">A steer ID the replacement withdraws: false when the run already accepted it, and refused afterward.</param>
    /// <param name="ct">Cancels the wait for the run.</param>
    public async Task<bool> TryReplaceAsync(ChatDisplayMessage message, string? withdrawnMessageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed || _onReplace is null || (withdrawnMessageId is not null && _acceptedIds.Contains(withdrawnMessageId)))
            {
                return false;
            }

            if (!await _onReplace(message, ct).ConfigureAwait(false))
            {
                return false;
            }

            if (withdrawnMessageId is not null)
            {
                _withdrawnIds.Add(withdrawnMessageId);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops taking steers and reports whether any still wait for a model call.</summary>
    public Task<bool> CloseAsync(CancellationToken ct) => CloseAsync(keepOpenWhilePending: false, ct);

    /// <summary>Stops taking steers only when none wait for a model call, and reports whether any do.</summary>
    /// <remarks>The model call a waiting steer needs can start subagents, which must stay steerable.</remarks>
    public Task<bool> CloseWhenIdleAsync(CancellationToken ct) => CloseAsync(keepOpenWhilePending: true, ct);

    private async Task<bool> CloseAsync(bool keepOpenWhilePending, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed)
            {
                return false;
            }

            var hasPending = (await _injector.GetPendingMessagesAsync(Session, ct).ConfigureAwait(false)).Count > 0;
            _closed = !(hasPending && keepOpenWhilePending);
            return hasPending;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reports the steers found in a model request as applied.</summary>
    internal async Task MarkAppliedAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var applied = new List<ChatDisplayMessage>();
        lock (_pending)
        {
            foreach (var message in messages)
            {
                if (message.MessageId is { } messageId && _pending.Remove(messageId, out var display))
                {
                    applied.Add(display);
                    _appliedTexts.Add(message.Text);
                }
            }
        }

        foreach (var display in applied)
        {
            await _onApplied(display, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Removes unconsumed steers from a saved checkpoint or a finished session.</summary>
    public static void RemovePendingMessages(AgentSession session) => session.StateBag.TryRemoveValue(PendingMessagesStateKey);

    public void Dispose()
    {
        _owner.Unregister(this);
        _gate.Wait();
        _closed = true;
        _gate.Release();
        // Steers the model never received must not be saved with the session.
        RemovePendingMessages(Session);
    }
}
