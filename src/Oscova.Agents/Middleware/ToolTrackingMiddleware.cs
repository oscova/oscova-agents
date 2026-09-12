using System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Middleware;

/// <summary>
/// Extension method that adds tool execution tracking to an <see cref="IChatClient"/> pipeline
/// via <see cref="FunctionInvokingChatClient"/>.
/// </summary>
public static class ToolTrackingMiddleware
{
    /// <summary>
    /// Adds function invocation tracking to the chat client pipeline. The <paramref name="accessor"/>
    /// provides the current <see cref="IToolTrackingObserver"/> which is notified of tool
    /// start, completion, and failure events.
    /// </summary>
    public static ChatClientBuilder UseToolTracking(
        this ChatClientBuilder builder,
        ToolTrackingObserverAccessor accessor,
        ILogger? logger = null)
    {
        return FunctionInvocationComposition.Contribute(builder, fic =>
        {
            var previousInvoker = fic.FunctionInvoker;
            fic.FunctionInvoker = async (context, ct) =>
            {
                // Build a FunctionCallContent from the invocation context so observers get a consistent
                // richer payload (name, call id, arguments) instead of just the function name.
                var functionCall = new FunctionCallContent(
                    context.CallContent?.CallId ?? string.Empty,
                    context.Function.Name,
                    context.Arguments);

                logger?.LogTrace("Tool invoked: {Name}", context.Function.Name);
                accessor.Observer?.OnToolStarted(functionCall);
                try
                {
                    var result = previousInvoker is null
                        ? await context.Function.InvokeAsync(context.Arguments, ct)
.ConfigureAwait(false) : await previousInvoker(context, ct).ConfigureAwait(false);
                    accessor.Observer?.OnToolCompleted(functionCall);
                    logger?.LogTrace("Tool completed: {Name}", context.Function.Name);
                    return result;
                }
                catch (Exception ex)
                {
                    accessor.Observer?.OnToolFailed(functionCall);
                    logger?.LogTrace(ex, "Tool failed: {Name}", context.Function.Name);
                    throw;
                }
            };
        });
    }
}
