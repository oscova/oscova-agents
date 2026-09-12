using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Executes a single agent task run. Implemented by the consuming application
    /// because it requires app-specific dependencies (AppService, chat clients, etc.)
    /// </summary>
    public interface IAgentTaskExecutor
    {
        /// <summary>
        /// Executes the task. If <paramref name="run"/> is provided the executor
        /// populates it in place and returns it; otherwise a new
        /// <see cref="AgentTaskRun"/> is created internally.
        /// </summary>
        Task<AgentTaskRun> ExecuteAsync(
            AgentTaskDefinition task,
            AgentTaskRun? run = null,
            CancellationToken ct = default);
    }
}
