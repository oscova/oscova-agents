using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Oscova.Agents.Harness;
using Oscova.Agents.Providers;

namespace Oscova.Agents.Toolsets;

/// <summary>Lets an agent pause its current run, or wait for selected subagents and background jobs.</summary>
/// <param name="resolveWork">Returns the root run's subagent state, or null outside a run that owns one.</param>
/// <param name="findJob">Returns the completion of a background job this conversation started in an earlier run, or null.</param>
public sealed class WaitToolset(Func<DelegatedWorkState?>? resolveWork = null, Func<string, Task?>? findJob = null) : IAgentToolset
{
    /// <summary>Identifies the wait tool, which delegation exposes with its subagent tools.</summary>
    public static string QualifiedToolName { get; } = ToolNaming.BuildToolsetQualifiedName<WaitToolset>(nameof(WaitAsync));

    /// <summary>Waits for a bounded duration or selected work, then tells the agent what it observed.</summary>
    [DisplayName("sleep")]
    [AgentTool]
    [Description("Pauses this run for at most durationSeconds, then returns so you can check again.\nWithout handles, a new user message ends the pause early.\nWith handles, it also returns when a selected subagent or background job finishes, fails, stops or needs approval.\nA timeout means the selected work is still pending.")]
    public async Task<string> WaitAsync(
        [Description("Maximum seconds to wait.\nValues below 1 use 1.\nValues above 300 use 300.")]
        int durationSeconds,
        [Description("Short reason for the pause and what will be checked afterward.")]
        string reason,
        [Description("Optional subagent handles or background job IDs to wait for.\nUse only handles started in this conversation.")]
        string[]? handles = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ToolFailureException("A reason for waiting is required.");
        }

        durationSeconds = Math.Clamp(durationSeconds, 1, 300);
        var timeout = TimeSpan.FromSeconds(durationSeconds);
        if (resolveWork?.Invoke() is { } work && AIAgent.CurrentRunContext is { Session: { } session } run)
        {
            // Jobs started in this run are already watched with their owner. Older jobs belong to the parent,
            // because a worker's jobs end with that worker.
            foreach (var handle in handles ?? [])
            {
                if (findJob?.Invoke(handle) is { } completion)
                {
                    work.Watch(handle, ownerWorkerHandle: null, completion);
                }
            }

            return await work.WaitAsync(run.Agent, session, handles ?? [], timeout, cancellationToken).ConfigureAwait(false);
        }

        if (handles is { Length: > 0 })
        {
            throw new ToolFailureException("Waiting on handles needs an active conversation run.", "Call sleep without handles.");
        }

        // Let invocation cancellation end the pause; the caller owns the run lifetime.
        await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
        return $"Waited {durationSeconds} seconds. Recheck the external state before continuing.";
    }
}
