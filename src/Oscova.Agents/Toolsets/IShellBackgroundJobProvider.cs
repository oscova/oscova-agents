using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Launches and tracks detached shell commands for the active conversation.
/// </summary>
/// <remarks>
/// Background shell jobs use an implementation-owned detached process because the framework shell
/// executor is a blocking command primitive and does not expose partial reads for long-running jobs.
/// </remarks>
public interface IShellBackgroundJobProvider
{
    /// <summary>
    /// Starts a shell command in the background for the active conversation.
    /// </summary>
    Task<StartJobResult> StartAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads output from a background shell job after the supplied absolute output offset.
    /// </summary>
    Task<ReadJobResult> ReadAsync(string jobId, long afterOffset, int? maxBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a background shell job.
    /// </summary>
    Task<KillJobResult> KillAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops and removes every background shell job owned by the conversation execution id.
    /// </summary>
    Task CleanupExecutionAsync(string executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops and removes every background shell job owned by the user.
    /// </summary>
    Task CleanupUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the conversation has at least one job that has not exited.
    /// </summary>
    bool HasLiveJobs(string executionId);
}

/// <summary>
/// Result returned when a background shell command is started.
/// </summary>
public sealed record StartJobResult(bool Started, string? JobId, string Message);

/// <summary>
/// Result returned when reading background shell command output.
/// </summary>
public sealed record ReadJobResult(
    bool Found,
    string Output,
    long NewOffset,
    long TotalSize,
    string Status,
    int? ExitCode,
    bool Truncated,
    string Message);

/// <summary>
/// Result returned when stopping a background shell command.
/// </summary>
public sealed record KillJobResult(bool Found, bool Killed, string Message);
