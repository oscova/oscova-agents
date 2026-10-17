using Oscova.Agents.Execution;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Tools.Shell;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Supplies the shell executor owned by the active host run context.
/// </summary>
public interface IShellExecutorProvider
{
    /// <summary>
    /// Gets or creates the executor for the current run.
    /// </summary>
    Task<ShellExecutor> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the current run has an executor and returns the target details used by background jobs.
    /// </summary>
    Task<IExecutionSession> GetSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks the original command and resolves a per-call working directory on the executing device.</summary>
    Task<string> PrepareCommandAsync(string command, string? workdir, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes and evicts the cached executor for the given conversation id, if any.
    /// </summary>
    Task EvictAsync(string executionId);

    /// <summary>
    /// Terminates the current foreground command and restores a clean shell after interruption.
    /// </summary>
    Task RecoverForegroundCommandAsync();
}
