using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Hooks;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Hooks;

/// <summary>
/// Builds runtime hook envelopes and dispatches them through the configured outbound hook pipeline.
/// </summary>
internal sealed class AgentRuntimeHookService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<AgentRuntimeHookService> logger) : IAgentRuntimeHookService
{
    public Task<HookDispatchResult> DispatchSessionStartAsync(SessionHookContext context, CancellationToken ct = default)
    {
        var envelope = BuildEnvelope(
            HookEventKind.SessionStart,
            context.UserId,
            context.AgentId,
            context.ChatThreadId,
            taskId: null,
            taskRunId: null,
            context.ExecutionKind,
            JsonSerializer.SerializeToElement(new
            {
                chat_thread_id = context.ChatThreadId,
                agent_id = context.AgentId,
                execution_kind = context.ExecutionKind,
                channel_id = context.ChannelId
            }, HookJsonSerializer.Options));

        return DispatchAsync(envelope, isBlocking: false, ct);
    }

    public async Task<HookDispatchResult> DispatchUserPromptSubmitAsync(
        PromptHookContext context,
        CancellationToken ct = default)
    {
        var eventPayload = JsonSerializer.SerializeToElement(new
        {
            chat_thread_id = context.ChatThreadId,
            agent_id = context.AgentId,
            execution_kind = context.ExecutionKind,
            channel_id = context.ChannelId
        }, HookJsonSerializer.Options);
        var envelope = BuildEnvelope(
            HookEventKind.UserPromptSubmit,
            context.UserId,
            context.AgentId,
            context.ChatThreadId,
            taskId: null,
            taskRunId: null,
            context.ExecutionKind,
            eventPayload);

        return await DispatchAsync(envelope, isBlocking: true, ct).ConfigureAwait(false);
    }

    public async Task<HookDispatchResult> DispatchPreToolUseAsync(
        ToolHookContext context,
        CancellationToken ct = default)
    {
        var envelope = BuildToolEnvelope(HookEventKind.PreToolUse, context, redactContent: true);
        return await DispatchAsync(envelope, isBlocking: true, ct).ConfigureAwait(false);
    }

    public async Task DispatchPostToolUseAsync(ToolHookContext context, CancellationToken ct = default)
    {
        var envelope = BuildToolEnvelope(HookEventKind.PostToolUse, context, redactContent: true);
        await DispatchAsync(envelope, isBlocking: false, ct).ConfigureAwait(false);
    }

    internal static ToolHookContext BuildToolContext(
        FunctionInvocationContext invocation,
        RunContext? runContext,
        string agentId,
        ILogger? logger = null)
    {
        var userId = runContext?.UserId ?? HookExecutionKinds.Unknown;
        var executionKind = runContext is null
            ? HookExecutionKinds.Unknown
            : HookExecutionKinds.FromRunExecutionKind(runContext.ExecutionKind);
        string? chatThreadId = null;
        string? taskId = null;
        string? taskRunId = null;

        if (runContext is null)
        {
            logger?.LogWarning("Tool hook context is missing the current run context.");
        }
        else if (runContext.Task is not null)
        {
            taskId = runContext.Task.TaskId;
            taskRunId = runContext.ExecutionId;
        }
        else
        {
            chatThreadId = runContext.ExecutionId;
        }

        return new ToolHookContext
        {
            UserId = userId,
            AgentId = agentId,
            ChatThreadId = chatThreadId,
            TaskId = taskId,
            TaskRunId = taskRunId,
            ExecutionKind = executionKind,
            ToolName = invocation.Function.Name,
            ToolCallId = invocation.CallContent?.CallId,
            Arguments = JsonSerializer.SerializeToElement(invocation.Arguments, HookJsonSerializer.Options)
        };
    }

    private async Task<HookDispatchResult> DispatchAsync(HookEnvelope envelope, bool isBlocking, CancellationToken ct)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IAgentHookDispatcher>();
            return await dispatcher.DispatchAsync(new HookDispatchRequest
            {
                Envelope = envelope,
                IsBlocking = isBlocking
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Runtime hook dispatch failed for '{EventName}'.", envelope.HookEventName);
            // Dispatcher infrastructure failed before any per-hook failure mode could apply.
            return isBlocking
                ? HookDispatchResult.TransportFailure("Hook dispatch failed.", "dispatch_failed")
                : HookDispatchResult.Allow();
        }
    }

    private static HookEnvelope BuildToolEnvelope(
        HookEventKind eventKind,
        ToolHookContext context,
        bool redactContent)
    {
        var eventPayload = redactContent
            ? JsonSerializer.SerializeToElement(new
            {
                tool_name = context.ToolName,
                tool_call_id = context.ToolCallId,
                result_kind = context.ResultKind,
                chat_thread_id = context.ChatThreadId,
                task_id = context.TaskId,
                task_run_id = context.TaskRunId,
                agent_id = context.AgentId,
                execution_kind = context.ExecutionKind
            }, HookJsonSerializer.Options)
            : JsonSerializer.SerializeToElement(new
            {
                tool_name = context.ToolName,
                tool_call_id = context.ToolCallId,
                arguments = context.Arguments,
                result_kind = context.ResultKind,
                error_message = context.ErrorMessage,
                reason = context.Reason,
                chat_thread_id = context.ChatThreadId,
                task_id = context.TaskId,
                task_run_id = context.TaskRunId,
                agent_id = context.AgentId,
                execution_kind = context.ExecutionKind
            }, HookJsonSerializer.Options);
        return BuildEnvelope(
            eventKind,
            context.UserId,
            context.AgentId,
            context.ChatThreadId,
            context.TaskId,
            context.TaskRunId,
            context.ExecutionKind,
            eventPayload);
    }

    private static HookEnvelope BuildEnvelope(
        HookEventKind eventKind,
        string? userId,
        string? agentId,
        string? chatThreadId,
        string? taskId,
        string? taskRunId,
        string? executionKind,
        JsonElement eventElement)
    {
        return new HookEnvelope
        {
            HookEventName = eventKind,
            UserId = userId,
            AgentId = agentId,
            ChatThreadId = chatThreadId,
            TaskId = taskId,
            TaskRunId = taskRunId,
            ExecutionKind = executionKind,
            Event = eventElement
        };
    }
}
