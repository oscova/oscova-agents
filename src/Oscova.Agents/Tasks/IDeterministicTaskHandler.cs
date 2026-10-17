using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Handles a reserved task end-to-end without invoking the standard agent chat pipeline.
/// </summary>
public interface IDeterministicTaskHandler
{
    /// <summary>
    /// Runs the task to completion. The executor will mark the run completed on return.
    /// </summary>
    Task ExecuteAsync(AgentTaskDefinition task, CancellationToken ct = default);
}
