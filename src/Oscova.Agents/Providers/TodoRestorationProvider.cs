using Microsoft.Agents.AI;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Providers;

/// <summary>Refreshes saved todos before the next model call after history compaction.</summary>
internal sealed class TodoRestorationProvider(TodoProvider todos) : AIContextProvider
{
    private readonly ConditionalWeakTable<AgentRunContext, object> _pendingRuns = new();

    // Cached agents serve multiple sessions, but restoration belongs only to the run that compacted.
    internal void MarkPending()
    {
        if (AIAgent.CurrentRunContext is { Session: not null } run)
        {
            _pendingRuns.GetValue(run, static _ => new object());
        }
    }

    protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var run = AIAgent.CurrentRunContext;
        if (run is null || !_pendingRuns.TryGetValue(run, out _)) { return context.AIContext; }

        // Read after intervening tools finish, using the configured provider's formatting and attribution.
#pragma warning disable MAAI001 // Refresh only the provider's messages through its existing invocation contract.
        var restored = await todos.InvokingAsync(new(context.Agent, context.Session, new AIContext()), cancellationToken).ConfigureAwait(false);
#pragma warning restore MAAI001
        var messages = (context.AIContext.Messages ?? []).Concat(restored.Messages ?? []).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        _pendingRuns.Remove(run);
        return new AIContext
        {
            Messages = messages,
            Instructions = context.AIContext.Instructions,
            Tools = context.AIContext.Tools
        };
    }
}
