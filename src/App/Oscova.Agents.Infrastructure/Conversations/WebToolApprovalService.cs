using Oscova.Agents.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>Owns pending web decisions and temporary grants in the retained user runtime.</summary>
public sealed class WebToolApprovalService(RunContextAccessor runs, ILogger<WebToolApprovalService> logger)
    : IWebToolApprovalService
{
    private readonly Dictionary<string, PendingToolApproval> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<GrantKey, SessionToolPermission> _grants = [];
    private readonly Lock _approvalLock = new();

    /// <inheritdoc />
    public ToolApprovalRequest RegisterToolApproval(ToolApprovalRequestContent request,
        ToolApprovalRequestContent displaySource, ToolApprovalReview? review, ChatThread chatThread, Action onStateChanged)
    {
        var display = CreateDisplay(displaySource, runs.Current, review);
        var key = CaptureKey(request, display, runs.Current, chatThread.Id);
        display.CanAllowForSession = key is not null;
        var message = new ChatDisplayMessage
        {
            Channel = HookExecutionKinds.WebChat, From = MessageOwner.Bot,
            AgentId = display.AgentId, ToolApproval = display
        };
        lock (_approvalLock)
        {
            _pending.Add(display.RequestId, new(request, message, onStateChanged, key));
            lock (chatThread.DisplayMessages) { chatThread.DisplayMessages.Add(message); }
        }

        onStateChanged.Invoke();
        return display;
    }

    /// <inheritdoc />
    public async Task<ToolApprovalSubmission> WaitForToolApprovalAsync(string requestId, CancellationToken token)
    {
        PendingToolApproval pending;
        lock (_approvalLock)
        {
            pending = _pending.GetValueOrDefault(requestId)
                ?? throw new InvalidOperationException($"Tool approval {requestId} has expired. Request the tool again.");
        }

        using var registration = token.Register(() => CancelPendingToolApproval(requestId));
        var result = await pending.Completion.Task.ConfigureAwait(false);
        lock (_approvalLock) { _pending.Remove(requestId); }

        return result;
    }

    /// <inheritdoc />
    public Task SubmitToolApprovalAsync(string requestId, ToolApprovalDecision decision)
    {
        if (!Enum.IsDefined(decision)) { throw new ArgumentException("Decision must be AllowOnce, AllowForSession or Deny."); }

        PendingToolApproval pending;
        lock (_approvalLock)
        {
            pending = _pending.GetValueOrDefault(requestId)
                ?? throw new InvalidOperationException($"Tool approval {requestId} has expired. Request the tool again.");
            if (pending.Completion.Task.IsCompleted)
            {
                throw new InvalidOperationException("This tool approval was already answered.");
            }

            if (decision == ToolApprovalDecision.AllowForSession && pending.Key is null)
            {
                throw new InvalidOperationException("This request has no trustworthy session scope. Choose Allow once or Deny.");
            }
            // Grant creation, cancellation and the terminal decision share the same lock.
            if (decision == ToolApprovalDecision.AllowForSession)
            {
                var key = pending.Key!;
                var display = pending.Message.ToolApproval!;
                _grants.TryAdd(key, new(Guid.NewGuid().ToString("N"), key.ToolName, key.AgentId,
                    display.AgentName, key.ProjectId, key.Device, key.Target));
            }

            pending.Message.ToolApproval!.Outcome = decision switch
            {
                ToolApprovalDecision.AllowOnce => ToolApprovalOutcome.AllowedOnce,
                ToolApprovalDecision.AllowForSession => ToolApprovalOutcome.AllowedForSession,
                _ => ToolApprovalOutcome.Denied
            };
            var approved = decision != ToolApprovalDecision.Deny;
            pending.Completion.SetResult(new(pending.Request.CreateResponse(approved,
                approved ? "Approved by user" : "Denied by user"), null));
        }

        logger.LogDebug("Resolved tool approval {RequestId} with {Decision}.", requestId, decision);
        pending.OnChanged.Invoke();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ToolApprovalSubmission? TryAutoApprove(ToolApprovalRequestContent request, ChatThread thread,
        Action onStateChanged, ToolApprovalRunMode mode)
    {
        var display = CreateDisplay(request, runs.Current);
        var key = CaptureKey(request, display, runs.Current, thread.Id);
        ToolApprovalOutcome? outcome;
        lock (_approvalLock)
        {
            outcome = mode switch
            {
                ToolApprovalRunMode.AutoRejectRequired => ToolApprovalOutcome.AutoRejected,
                ToolApprovalRunMode.AutoApproveRequired => runs.Current?.PermissionPreset == ChatPermissionPreset.FullAccess
                    ? ToolApprovalOutcome.FullAccess : ToolApprovalOutcome.AutoApproved,
                _ when key is not null && _grants.ContainsKey(key) => ToolApprovalOutcome.SessionPermission,
                _ => null
            };
            if (outcome is null) { return null; }
            // Only structured metadata survives automatic approval. Arguments remain on the original call.
            display.Arguments = null;
            display.Message = string.Empty;
            display.Review = null;
            display.Outcome = outcome;
            lock (thread.DisplayMessages)
            {
                thread.DisplayMessages.Add(new ChatDisplayMessage
                {
                    Channel = HookExecutionKinds.WebChat, From = MessageOwner.Bot,
                    AgentId = display.AgentId, ToolApproval = display
                });
            }
        }

        onStateChanged.Invoke();
        return new(request.CreateResponse(outcome != ToolApprovalOutcome.AutoRejected, outcome.ToString()), null);
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionToolPermission> GetSessionPermissions(string conversationId)
    {
        lock (_approvalLock)
        {
            return _grants.Where(item => item.Key.ConversationId == conversationId).Select(item => item.Value).ToArray();
        }
    }

    /// <inheritdoc />
    public void RevokeSessionPermission(string conversationId, string? grantId)
    {
        lock (_approvalLock)
        {
            foreach (var key in _grants.Where(item => item.Key.ConversationId == conversationId &&
                (grantId is null || item.Value.Id == grantId)).Select(item => item.Key).ToArray())
            {
                _grants.Remove(key);
            }
        }
    }

    /// <inheritdoc />
    public bool IsPending(string requestId)
    {
        lock (_approvalLock) { return _pending.TryGetValue(requestId, out var pending) && !pending.Completion.Task.IsCompleted; }
    }

    /// <inheritdoc />
    public void CancelPendingToolApproval(string requestId)
    {
        PendingToolApproval? pending;
        lock (_approvalLock)
        {
            if (!_pending.TryGetValue(requestId, out pending) || pending.Completion.Task.IsCompleted) { return; }

            pending.Message.ToolApproval!.Outcome = ToolApprovalOutcome.Expired;
            pending.Completion.SetResult(new(pending.Request.CreateResponse(false, "Cancelled"), null));
        }

        pending.OnChanged.Invoke();
    }

    /// <inheritdoc />
    public void CancelAllPendingToolApprovals()
    {
        string[] ids;
        lock (_approvalLock) { ids = [.. _pending.Keys]; }

        foreach (var id in ids) { CancelPendingToolApproval(id); }
    }

    private static ToolApprovalRequest CreateDisplay(ToolApprovalRequestContent request, RunContext? run,
        ToolApprovalReview? review = null)
    {
        var display = ToolApprovalRequestBuilder.BuildDisplayRequest(request, run, review);
        display.RequestId = Guid.NewGuid().ToString("N");
        display.ToolCallId = (request.ToolCall as FunctionCallContent)?.CallId;
        if (display.Review?.Kind is "shell" or "skill-script")
        {
            display.ExecutionDevice = run?.ExecutionDevice;
            display.ExecutionTarget = display.Review.RunMode == "host" || display.Review.RunMode == "project"
                ? "Host" : run?.ExecutionProviderId;
        }

        return display;
    }

    private static GrantKey? CaptureKey(ToolApprovalRequestContent request, ToolApprovalRequest display,
        RunContext? run, string conversationId)
    {
        if (request.ToolCall is not FunctionCallContent call || string.IsNullOrWhiteSpace(call.Name) ||
            run is null || string.IsNullOrWhiteSpace(run.UserId) || string.IsNullOrWhiteSpace(run.CurrentAgentId) ||
            run.ExecutionKind != RunExecutionKind.WebChat || string.IsNullOrWhiteSpace(conversationId)) { return null; }

        var execution = display.Review?.Kind is "shell" or "skill-script";
        if (execution && (string.IsNullOrWhiteSpace(run.ExecutionDevice) ||
            run.ExecutionProviderId == null)) { return null; }
        // The original function identity and executing child supply authority, never browser display fields.
        return new(run.UserId, conversationId, run.CurrentAgentId, call.Name,
            execution ? run.ProjectId : null, execution ? display.ExecutionDevice : null,
            execution ? display.ExecutionTarget : null);
    }

    private sealed record GrantKey(string UserId, string ConversationId, string AgentId, string ToolName,
        string? ProjectId, string? Device, string? Target);

    private sealed class PendingToolApproval(ToolApprovalRequestContent request, ChatDisplayMessage message,
        Action onChanged, GrantKey? key)
    {
        public ToolApprovalRequestContent Request { get; } = request;
        public ChatDisplayMessage Message { get; } = message;
        public Action OnChanged { get; } = onChanged;
        public GrantKey? Key { get; } = key;
        public TaskCompletionSource<ToolApprovalSubmission> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
