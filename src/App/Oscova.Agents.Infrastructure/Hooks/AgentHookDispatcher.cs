using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Hooks;

namespace Oscova.Agents.Infrastructure.Hooks;

internal sealed class AgentHookDispatcher(
    HookSettings settings,
    CommandHookExecutor commandExecutor,
    HttpHookExecutor httpExecutor,
    ILogger<AgentHookDispatcher> logger) : IAgentHookDispatcher
{
    public async Task<HookDispatchResult> DispatchAsync(HookDispatchRequest request, CancellationToken ct = default)
    {
        var hooks = await settings.RunExclusiveAsync(async () =>
        {
            await settings.LoadAsync().ConfigureAwait(false);
            return settings.Hooks
                .Where(hook => hook.IsEnabled && hook.Events.Contains(request.Envelope.HookEventName))
                .ToList();
        }, ct).ConfigureAwait(false);

        foreach (var hook in hooks)
        {
            var result = await ExecuteHookAsync(hook, request.Envelope, ct).ConfigureAwait(false);
            if (result.IsTransportFailure)
            {
                logger.LogWarning(
                    "Hook '{HookId}' reported a transport failure for '{EventName}' with reason '{Reason}'.",
                    hook.Id,
                    request.Envelope.HookEventName,
                    result.Reason);
                result = MapFailure(hook, result);
            }

            if (request.IsBlocking && !result.IsAllowed)
            {
                return result;
            }
        }

        return HookDispatchResult.Allow();
    }

    private async Task<HookDispatchResult> ExecuteHookAsync(
        HookDefinition hook,
        HookEnvelope envelope,
        CancellationToken ct)
    {
        try
        {
            return hook.HandlerType switch
            {
                HookHandlerType.Command => await commandExecutor.ExecuteAsync(hook, envelope, ct).ConfigureAwait(false),
                HookHandlerType.Http => await httpExecutor.ExecuteAsync(hook, envelope, ct).ConfigureAwait(false),
                _ => HookDispatchResult.TransportFailure("Unsupported hook handler type.", "unsupported_handler")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Hook '{HookId}' failed while handling '{EventName}'.", hook.Id, envelope.HookEventName);
            return HookDispatchResult.TransportFailure("Hook execution failed.", "execution_failed");
        }
    }

    private static HookDispatchResult MapFailure(HookDefinition hook, HookDispatchResult failure)
    {
        return hook.FailureMode == HookFailureMode.FailOpen
            ? HookDispatchResult.Allow()
            : HookDispatchResult.Deny(failure.Message ?? "Hook execution failed.", failure.Reason);
    }
}
