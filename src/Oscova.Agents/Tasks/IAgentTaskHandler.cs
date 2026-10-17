using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Hooks into the standard agent task run for a registered task.
/// </summary>
public interface IAgentTaskHandler
{
    /// <summary>
    /// Prepares the agent run. Return <c>null</c> to indicate there is no work this tick;
    /// the executor will mark the run completed without invoking the agent.
    /// </summary>
    Task<AgentRunPlan?> PrepareAsync(AgentTaskDefinition task, CancellationToken ct = default);
}
