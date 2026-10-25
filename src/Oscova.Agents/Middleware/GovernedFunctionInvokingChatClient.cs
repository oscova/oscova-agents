using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Hooks;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Middleware;

/// <summary>Keeps run-level tool governance denials outside the framework's retryable tool-error path
/// and marks responses whose tool loop was cut off at the iteration limit.</summary>
internal sealed class GovernedFunctionInvokingChatClient(
    IChatClient innerClient,
    ILoggerFactory? loggerFactory,
    IServiceProvider? functionInvocationServices)
    : FunctionInvokingChatClient(new InnerRequestCounter(innerClient), loggerFactory, functionInvocationServices)
{
    private const string IterationLimitReachedKey = "oscova.iteration_limit_reached";

    /// <summary>Reports whether a response stopped because its tool loop reached the iteration limit.</summary>
    internal static bool ReachedIterationLimit(AdditionalPropertiesDictionary? properties) =>
        properties is not null && properties.TryGetValue(IterationLimitReachedKey, out var reached) && reached is true;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var requests = new StrongBox<int>();
        ((InnerRequestCounter)InnerClient).Requests.Value = requests;
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        // The framework answers the cap with one extra tool-less request, so only a cut-off loop exceeds the limit.
        if (requests.Value > MaximumIterationsPerRequest)
        {
            (response.AdditionalProperties ??= [])[IterationLimitReachedKey] = true;
        }

        return response;
    }

    protected override IList<ChatMessage> CreateResponseMessages(
        ReadOnlySpan<FunctionInvocationResult> results)
    {
        var terminates = false;
        foreach (var result in results)
        {
            // These decisions abort the run and must reach the host instead of becoming model retry input.
            if (result.Exception is IRunAbortException)
            {
                ExceptionDispatchInfo.Capture(result.Exception).Throw();
            }

            terminates |= result.Terminate;
        }

        var messages = base.CreateResponseMessages(results);
        var session = AIAgent.CurrentRunContext?.Session;
        if (terminates && session is not null && session.TryGetInMemoryChatHistory(out var history))
        {
            // A terminating tool has no later model call to persist its result batch.
            history.AddRange(messages);
        }

        return messages;
    }

    /// <summary>Counts model requests made within the current non-streaming call of the owning client.</summary>
    private sealed class InnerRequestCounter(IChatClient innerClient) : DelegatingChatClient(innerClient)
    {
        public AsyncLocal<StrongBox<int>?> Requests { get; } = new();

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Requests.Value is { } requests)
            {
                requests.Value++;
            }

            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}
