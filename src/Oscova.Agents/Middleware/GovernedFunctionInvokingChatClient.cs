using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Hooks;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace Oscova.Agents.Middleware;

/// <summary>Keeps run-level tool governance denials outside the framework's retryable tool-error path.</summary>
internal sealed class GovernedFunctionInvokingChatClient(
    IChatClient innerClient,
    ILoggerFactory? loggerFactory,
    IServiceProvider? functionInvocationServices)
    : FunctionInvokingChatClient(innerClient, loggerFactory, functionInvocationServices)
{
    protected override IList<ChatMessage> CreateResponseMessages(
        ReadOnlySpan<FunctionInvocationResult> results)
    {
        foreach (var result in results)
        {
            // These decisions abort the run and must reach the host instead of becoming model retry input.
            if (result.Exception is IRunAbortException)
            {
                ExceptionDispatchInfo.Capture(result.Exception).Throw();
            }
        }

        return base.CreateResponseMessages(results);
    }
}