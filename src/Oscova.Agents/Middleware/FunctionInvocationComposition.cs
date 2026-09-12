using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Middleware;

/// <summary>
/// Composes function-invocation middleware onto a single <see cref="FunctionInvokingChatClient"/>.
/// Each call to <c>UseFunctionInvocation</c> creates a new FICC, and once a FICC sees a
/// <see cref="FunctionCallContent"/> it buffers every later streaming update until the inner
/// stream completes. Stacking FICCs therefore makes the outer ones hold back the entire post-tool
/// answer and release it in one burst. Routing the per-tool configurators through a single FICC
/// keeps the stream flowing while preserving the existing invoker composition.
/// </summary>
public static class FunctionInvocationComposition
{
    private static readonly AsyncLocal<ICollection<Action<FunctionInvokingChatClient>>?> Collector = new();

    /// <summary>
    /// Begins a composition scope. While active, <see cref="Contribute"/> records configurators into
    /// <paramref name="configurators"/> instead of adding a separate FICC to the builder. Dispose to end.
    /// </summary>
    public static IDisposable Begin(ICollection<Action<FunctionInvokingChatClient>> configurators)
    {
        var previous = Collector.Value;
        Collector.Value = configurators;
        return new Scope(previous);
    }

    /// <summary>
    /// Records <paramref name="configure"/> into the active composition (returning the builder unchanged),
    /// or adds a standalone <see cref="FunctionInvokingChatClient"/> when no composition is active.
    /// </summary>
    public static ChatClientBuilder Contribute(ChatClientBuilder builder, Action<FunctionInvokingChatClient> configure)
    {
        if (Collector.Value is { } collector)
        {
            collector.Add(configure);
            return builder;
        }

        return builder.UseFunctionInvocation(configure: configure);
    }

    private sealed class Scope(ICollection<Action<FunctionInvokingChatClient>>? previous) : IDisposable
    {
        public void Dispose() => Collector.Value = previous;
    }
}
