using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Tools.Shell;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Exposes foreground and background shell command execution through the runtime selected by the host app.
/// </summary>
public sealed class ShellToolset(
    IShellExecutorProvider executorProvider,
    IShellBackgroundJobProvider backgroundJobs) : ICrewToolset
{
    // Per-call timeout bounds: default 120s when unspecified, hard cap 600s (10 minutes).
    private const int MinTimeoutSeconds = 1;
    private const int DefaultTimeoutSeconds = 120;

    /// <summary>The maximum seconds a single command may run; also the shared executor backstop.</summary>
    public const int MaxTimeoutSeconds = 600;

    /// <summary>
    /// Executes a command through the current run's shell executor. Approval is required by default.
    /// </summary>
    /// <param name="command">The shell command to execute.</param>
    /// <param name="timeoutSeconds">Optional per-call timeout in seconds; defaults and clamps applied.</param>
    /// <param name="cancellationToken">A token that can cancel command execution.</param>
    /// <returns>The command exit code, output streams, timeout flag, and truncation flag.</returns>
    [DisplayName("exec_command")]
    [Description("Runs a shell command on the configured execution target. Working-directory persistence depends on the executor. Chain cd with the command when a particular directory is needed. Returns stdout, stderr, exit code, timeout, and truncation status.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    [ToolNetworkScope(NetworkScope.Incidental)]
    public async Task<object> ExecCommandAsync(
        [Description("The shell command to execute")] string command,
        [Description("Optional. Maximum seconds the command may run before it is terminated. Default 120, maximum 600 (10 minutes). If it times out or the run is cancelled, the shell session is reset, so state from earlier calls (such as the working directory) is lost.")] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ToolFailureException("A shell command is required.");
        }
        // Resolve (and start) the executor before arming the timer so startup latency is not counted.
        ShellExecutor executor = await executorProvider.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteCommandAsync(
            executor,
            executorProvider.RecoverForegroundCommandAsync,
            command,
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executes one command with the shared result, timeout, and recovery contract.</summary>
    internal static async Task<object> ExecuteCommandAsync(
        ShellExecutor executor,
        Func<Task> recoverAsync,
        string command,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        int effectiveSeconds = Math.Clamp(timeoutSeconds ?? DefaultTimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);

        // The framework's per-command timeout is fixed at executor construction and its graceful
        // interrupt is internal, so we enforce the per-call timeout here with a linked token.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(effectiveSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            ShellResult result = await executor.RunAsync(command, linkedCts.Token).ConfigureAwait(false);
            return new
            {
                exitCode = result.ExitCode,
                stdout = result.Stdout,
                stderr = result.Stderr,
                timedOut = result.TimedOut,
                truncated = result.Truncated
            };
        }
        // Our per-call timer fired (not a caller/run cancellation). The framework leaves the command
        // running in the persistent shell, so reset the session to terminate it before the next call.
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await recoverAsync().ConfigureAwait(false);
            return new
            {
                exitCode = 124,
                stdout = string.Empty,
                stderr = $"Command terminated after exceeding the {effectiveSeconds}s timeout.",
                timedOut = true,
                truncated = false
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await recoverAsync().ConfigureAwait(false);
            throw;
        }
        catch (ShellCommandRejectedException)
        {
            throw new ToolFailureException("Shell command was rejected by the execution policy.");
        }
    }

    /// <summary>
    /// Launches a shell command in the background and returns a job id immediately.
    /// </summary>
    [DisplayName("start_command")]
    [Description("Launches a separate shell command in the configured working directory and returns a job id immediately. Use for dev servers, watchers, or long builds. Read its output later with read_command_output and stop it with kill_command.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    [ToolNetworkScope(NetworkScope.Incidental)]
    public async Task<object> StartCommandAsync(
        [Description("The shell command to run in the background")] string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ToolFailureException("A shell command is required.");
        }

        StartJobResult result = await backgroundJobs.StartAsync(command, cancellationToken).ConfigureAwait(false);
        if (!result.Started)
        {
            throw new ToolFailureException("The background command could not be started.", "Check the command and try again.");
        }

        return new
        {
            started = result.Started,
            jobId = result.JobId,
            message = result.Message
        };
    }

    /// <summary>
    /// Reads incremental output from a background shell command.
    /// </summary>
    [DisplayName("read_command_output")]
    [Description("Reads new output from a background command started with start_command. Pass the offset you last saw (0 to start) to get output after it; returns the new offset, total size, status, and exit code.")]
    public async Task<object> ReadCommandOutputAsync(
        [Description("The job id from start_command")] string jobId,
        [Description("Offset already seen; pass 0 the first time, then the newOffset from the previous read")] long afterOffset = 0,
        [Description("Optional max bytes to return this read")] int? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ToolFailureException("A shell job ID is required.");
        }

        int? effectiveMaxBytes = maxBytes > 0 ? maxBytes : null;
        ReadJobResult result = await backgroundJobs.ReadAsync(jobId, Math.Max(0, afterOffset), effectiveMaxBytes, cancellationToken).ConfigureAwait(false);
        if (!result.Found)
        {
            throw new ToolFailureException("The shell job was not found.", "Use the job ID returned by start_command.");
        }

        return new
        {
            found = result.Found,
            output = result.Output,
            newOffset = result.NewOffset,
            totalSize = result.TotalSize,
            status = result.Status,
            exitCode = result.ExitCode,
            truncated = result.Truncated,
            message = result.Message
        };
    }

    /// <summary>
    /// Stops a background shell command.
    /// </summary>
    [DisplayName("kill_command")]
    [Description("Stops a background command started with start_command.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    public async Task<object> KillCommandAsync(
        [Description("The job id from start_command")] string jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ToolFailureException("A shell job ID is required.");
        }

        KillJobResult result = await backgroundJobs.KillAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!result.Found)
        {
            throw new ToolFailureException("The shell job was not found.", "Use the job ID returned by start_command.");
        }

        return new
        {
            found = result.Found,
            killed = result.Killed,
            message = result.Message
        };
    }
}
