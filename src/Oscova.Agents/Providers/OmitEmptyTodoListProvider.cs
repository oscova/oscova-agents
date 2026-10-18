using Microsoft.Agents.AI;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Providers;

/// <summary>Adds the todo tools and instructions every turn, but the todo list message only once a todo is saved.</summary>
/// <remarks>The framework provider always injects a list message, so an empty list would cost tokens and history on every turn.</remarks>
public sealed class OmitEmptyTodoListProvider(TodoProvider todos) : AIContextProvider
{
    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => todos.StateKeys;

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        base.GetService(serviceType, serviceKey) ?? todos.GetService(serviceType, serviceKey);

    /// <inheritdoc />
    protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
#pragma warning disable MAAI001 // Delegate to the framework provider's own merging and attribution.
        var merged = await todos.InvokingAsync(context, cancellationToken).ConfigureAwait(false);
#pragma warning restore MAAI001
        if (context.Session is null || (await todos.GetAllTodosAsync(context.Session, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return merged;
        }

        // Keep the incoming messages and drop only the provider's empty list message.
        merged.Messages = context.AIContext.Messages;
        return merged;
    }
}
