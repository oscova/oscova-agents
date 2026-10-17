using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Oscova.Agents.Harness;

namespace Oscova.Agents.Toolsets;

/// <summary>Lets an agent pause its current run before rechecking external work.</summary>
public sealed class WaitToolset : IAgentToolset
{
    /// <summary>Waits for a bounded duration, then tells the agent to recheck external state.</summary>
    [DisplayName("sleep")]
    [AgentTool]
    [Description("Pauses the current agent run for a bounded duration when external work has no status handle, then returns so the agent can check again.")]
    public static async Task<string> WaitAsync(
        [Description("Seconds to wait. Values below 1 use 1; values above 300 use 300.")]
        int durationSeconds,
        [Description("Short reason for the pause and what will be checked afterward.")]
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ToolFailureException("A reason for waiting is required.");
        }

        durationSeconds = Math.Clamp(durationSeconds, 1, 300);
        // Let invocation cancellation end the pause; the caller owns the run lifetime.
        await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);
        return $"Waited {durationSeconds} seconds. Recheck the external state before continuing.";
    }
}
