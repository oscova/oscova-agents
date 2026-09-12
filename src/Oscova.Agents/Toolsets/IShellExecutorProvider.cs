
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Tools.Shell;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Identifies where shell commands for the active run execute.
/// </summary>
public enum ShellTargetKind
{
    /// <summary>
    /// Commands run on the host machine through the resolved local shell.
    /// </summary>
    Local,

    /// <summary>
    /// Commands run inside the per-conversation Docker sandbox container.
    /// </summary>
    Docker
}

/// <summary>
/// Describes the shell execution target shared by foreground and background shell tools.
/// </summary>
public sealed record ShellExecutionTarget(
    ShellTargetKind Kind,
    string WorkspaceDirectory,
    string? ContainerName,
    string? DockerBinary,
    string? ShellBinary);

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
    Task<ShellExecutionTarget> GetExecutionTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes and evicts the cached executor for the given conversation id, if any.
    /// </summary>
    Task EvictAsync(string executionId);

    /// <summary>
    /// Terminates the current foreground command and restores a clean shell after interruption.
    /// </summary>
    Task RecoverForegroundCommandAsync();
}
