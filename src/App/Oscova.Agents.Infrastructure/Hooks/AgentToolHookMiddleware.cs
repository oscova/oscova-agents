using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.ToolGovernance;
using Oscova.Agents.Hooks;
using Oscova.Agents.Infrastructure.Mcp;
using Oscova.Agents.Middleware;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;
using Oscova.Agents.Runtime;

namespace Oscova.Agents.Infrastructure.Hooks;

/// <summary>
/// Adds external pre- and post-tool hook dispatch to the Microsoft.Extensions.AI function invocation loop.
/// </summary>
public static class AgentToolHookMiddleware
{
    public static ChatClientBuilder UseAgentToolHooks(
        this ChatClientBuilder builder,
        IAgentRuntimeHookService hookService,
        RunContextAccessor runContextAccessor,
        string agentId,
        ILogger? logger = null)
    {
        return builder.UseAgentToolHooks(
            hookService,
            runContextAccessor,
            agentId,
            toolGovernanceSettings: null,
            quotaUsageStore: null,
            networkScopeResolver: null,
            logger: logger);
    }

    public static ChatClientBuilder UseAgentToolHooks(
        this ChatClientBuilder builder,
        IAgentRuntimeHookService hookService,
        RunContextAccessor runContextAccessor,
        string agentId,
        ToolGovernanceSettings? toolGovernanceSettings = null,
        IToolQuotaUsageStore? quotaUsageStore = null,
        Func<string, NetworkScope>? networkScopeResolver = null,
        ILogger? logger = null)
    {
        return FunctionInvocationComposition.Contribute(builder, fic =>
        {
            var previousInvoker = fic.FunctionInvoker;
            fic.FunctionInvoker = async (FunctionInvocationContext context, CancellationToken ct) =>
                await InvokeFunctionWithHooksAsync(
                    context,
                    hookService,
                    runContextAccessor,
                    agentId,
                    logger,
                    previousInvoker,
                    toolGovernanceSettings,
                    quotaUsageStore,
                    networkScopeResolver,
                    ct).ConfigureAwait(false);
        });
    }

    internal static async ValueTask<object?> InvokeFunctionWithHooksAsync(
        FunctionInvocationContext context,
        IAgentRuntimeHookService hookService,
        RunContextAccessor runContextAccessor,
        string agentId,
        ILogger? logger,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? previousInvoker,
        ToolGovernanceSettings? toolGovernanceSettings,
        IToolQuotaUsageStore? quotaUsageStore,
        Func<string, NetworkScope>? networkScopeResolver,
        CancellationToken ct)
    {
        var hookContext = AgentRuntimeHookService.BuildToolContext(context, runContextAccessor.Current, agentId, logger);
        var networkScope = networkScopeResolver?.Invoke(context.Function.Name) ?? NetworkScope.None;

        // Metrics keep ids, never the server or agent display names inside qualified tool names.
        var mcpTool = context.Function.AdditionalProperties.GetValueOrDefault(McpServerHub.ToolMetadataProperty) as ExternalToolMetadata;
        var calledAgentId = context.Function.AdditionalProperties.TryGetValue(AgentAsToolProvider.AgentIdProperty, out var value)
            ? value as string
            : null;
        var recordedToolName = mcpTool?.OriginalName ?? (calledAgentId is null ? context.Function.Name : string.Empty);
        var decision = await hookService.DispatchPreToolUseAsync(hookContext, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await hookService.DispatchPostToolUseAsync(
                hookContext with
                {
                    ResultKind = "denied",
                    ErrorMessage = decision.Message ?? decision.Reason,
                    Reason = decision.Reason
                },
                CancellationToken.None).ConfigureAwait(false);

            context.Terminate = true;
            throw new HookDeniedException(
                HookEventKind.PreToolUse,
                context.Function.Name,
                decision.Message ?? decision.Reason ?? "Tool use was denied by a hook.",
                decision.Reason);
        }

        var quotaLimit = toolGovernanceSettings?.Find(context.Function.Name)?.CallLimit;
        if (quotaLimit is { IsEnabled: true })
        {
            if (quotaUsageStore is null)
            {
                throw new InvalidOperationException("Tool quota enforcement is configured but no quota usage store is available.");
            }

            var userId = runContextAccessor.Current?.UserId;
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new InvalidOperationException("Tool quota enforcement requires an active run context user.");
            }

            var quotaDecision = await quotaUsageStore.TryReserveAsync(
                userId,
                context.Function.Name,
                quotaLimit,
                DateTimeOffset.UtcNow,
                ct).ConfigureAwait(false);
            if (!quotaDecision.IsAllowed)
            {
                // Hosts log this message, so it names the MCP server or called agent by id.
                var quotaToolName = mcpTool?.ServerId is { } serverId
                    ? serverId + ToolNaming.WireSeparator + mcpTool.OriginalName
                    : calledAgentId ?? context.Function.Name;
                var quotaException = new ToolQuotaExceededException(quotaToolName, quotaDecision);
                await hookService.DispatchPostToolUseAsync(
                    hookContext with
                    {
                        ResultKind = "denied",
                        ErrorMessage = quotaException.Message,
                        Reason = "quota_exceeded"
                    },
                    CancellationToken.None).ConfigureAwait(false);

                runContextAccessor.Current?.ToolInvocationLedger?.RecordFailed(recordedToolName, networkScope, dispatched: false, mcpTool?.ServerId, calledAgentId);
                context.Terminate = true;
                throw quotaException;
            }
        }

        object? result;
        try
        {
            result = previousInvoker is null
                ? await context.Function.InvokeAsync(context.Arguments, ct)
.ConfigureAwait(false) : await previousInvoker(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await hookService.DispatchPostToolUseAsync(
                hookContext with
                {
                    ResultKind = "failed",
                    ErrorMessage = ex.Message
                },
                CancellationToken.None).ConfigureAwait(false);
            runContextAccessor.Current?.ToolInvocationLedger?.RecordFailed(recordedToolName, networkScope, mcpServerId: mcpTool?.ServerId, agentId: calledAgentId);
            logger?.LogTrace(ex, "Tool hook observed failed tool {ToolName}.", context.Function.Name);
            throw;
        }

        runContextAccessor.Current?.ToolInvocationLedger?.RecordCompleted(recordedToolName, networkScope, mcpTool?.ServerId, calledAgentId);
        await hookService.DispatchPostToolUseAsync(
            hookContext with { ResultKind = "succeeded" },
            ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
