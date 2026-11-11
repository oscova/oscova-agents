using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Execution;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Harness;

namespace Oscova.Agents.Plugins.Docker;
/// <summary>Owns Docker process groups and their bounded container output independently of foreground shells.</summary>
internal sealed class DockerBackgroundJobs(ILogger logger)
{
    private static readonly TimeSpan DockerPgidWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);
    private static string CreateJobId() => "shell-" + Guid.NewGuid().ToString("N")[..12];
    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    public async Task<IExecutionBackgroundJob> StartAsync(string command, DockerShellExecutor executor,
        IReadOnlyDictionary<string, string?> environment, BoundedOutputBuffer buffer, CancellationToken cancellationToken)
    {
        string containerName = executor.ContainerName
            ?? throw new InvalidOperationException("Docker shell execution target did not provide a container name.");
        string jobId = CreateJobId();
        string outputPath = $"/tmp/oscova-bg-{jobId}.out";
        string exitPath = $"/tmp/oscova-bg-{jobId}.exit";
        // Capture partial bytes and publish the count with its bounded payload in one atomic snapshot.
        string wrapper =
            $"LIMIT={BoundedOutputBuffer.DefaultCapacityBytes}; TOTAL=0; STATUS_FILE={QuotePosix(exitPath + ".status")}; " +
            $"rm -f {outputPath} {outputPath}.tmp {outputPath}.chunk {exitPath} \"$STATUS_FILE\"; " +
            "echo \"PGID:$$\"; " +
            $"{{ bash -lc {QuotePosix(command)}; printf '%s' \"$?\" > \"$STATUS_FILE\"; }} 2>&1 | " +
            $"while dd bs=65536 count=1 of={outputPath}.chunk status=none && [ -s {outputPath}.chunk ]; do " +
            $"BYTES=$(wc -c < {outputPath}.chunk); " +
            "TOTAL=$((TOTAL + BYTES)); " +
            $"{{ printf '%s\\n' \"$TOTAL\"; {{ tail -n +2 {outputPath} 2>/dev/null; cat {outputPath}.chunk; }} | tail -c \"$LIMIT\"; }} > {outputPath}.tmp && mv {outputPath}.tmp {outputPath}; " +
            "done; " +
            $"rm -f {outputPath}.chunk; " +
            $"if [ -f \"$STATUS_FILE\" ]; then cat \"$STATUS_FILE\" > {exitPath}; rm -f \"$STATUS_FILE\"; else printf '%s' \"1\" > {exitPath}; fi";
        var startInfo = new ProcessStartInfo
        {
            FileName = executor.DockerBinary ?? "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        foreach (var entry in environment.Where(entry => entry.Value is not null))
        { startInfo.ArgumentList.Add("-e"); startInfo.ArgumentList.Add(entry.Key + "=" + entry.Value); }
        startInfo.ArgumentList.Add(containerName);
        startInfo.ArgumentList.Add("setsid");
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(wrapper);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var job = new BackgroundJob(jobId, process, containerName, executor.DockerBinary ?? "docker")
        {
            DockerOutputPath = outputPath,
            DockerExitPath = exitPath
        };
        job.Buffer = buffer;
        job.Process.OutputDataReceived += (_, args) => { if (args.Data is not null) { job.TryCapturePgid(args.Data); } };
        job.Process.ErrorDataReceived += (_, args) => { if (args.Data is not null) { job.AppendLine(args.Data); } };
        job.Process.Exited += (_, _) => _ = CompleteDockerJobAsync(job);
        StartProcess(job);

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(DockerPgidWait);
        try
        {
            // A Docker job needs its process group before it can be exposed for management.
            if (await job.WaitForPgidAsync(DockerPgidWait, waitCts.Token).ConfigureAwait(false) is null) { throw new TimeoutException(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopDockerJobAsync(job, CancellationToken.None).ConfigureAwait(false);
            job.DisposeProcess();
            await CleanupAsync(job).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (ex is OperationCanceledException or TimeoutException))
        {
            logger.LogWarning(
                ex,
                "Background shell job {JobId} in container {ContainerName} did not report a process group within {TimeoutSeconds} seconds.",
                job.JobId,
                containerName,
                DockerPgidWait.TotalSeconds);

            await StopDockerJobAsync(job, CancellationToken.None).ConfigureAwait(false);
            job.DisposeProcess();
            await CleanupAsync(job).ConfigureAwait(false);
            throw new ToolFailureException("The Docker background command could not be confirmed.", "Check the sandbox and try again.");
        }

        return new Handle(job, this);
    }

    private async Task CompleteDockerJobAsync(BackgroundJob job)
    {
        try
        {
            job.Process.WaitForExit();
            // The host docker-exec process can exit while the setsid process group keeps running.
            // Only the in-container exit file is authoritative for Docker job completion.
            await RefreshDockerJobAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
        {
            logger.LogWarning(ex, "Could not refresh completed Docker background shell job {JobId}.", job.JobId);
            job.MarkExited(null);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not refresh completed Docker background shell job {JobId}.", job.JobId);
        }
        finally
        {
            job.DisposeProcess();

        }
    }

    private static void StartProcess(BackgroundJob job)
    {
        try
        {
            _ = job.Process.Start();
            job.Process.BeginOutputReadLine();
            job.Process.BeginErrorReadLine();
        }
        catch
        {
            job.DisposeProcess();
            throw;
        }
    }

    private static async Task StopDockerJobAsync(BackgroundJob job, CancellationToken cancellationToken)
    {
        long? pgid = await job.WaitForPgidAsync(DockerPgidWait, cancellationToken).ConfigureAwait(false);
        if (pgid is null)
        {
            job.KillHostProcess();
            job.MarkKilled();
            return;
        }

        await KillDockerProcessGroupAsync(job, pgid.Value, cancellationToken).ConfigureAwait(false);
        job.MarkKilled();
        job.MarkExited(null);
    }

    private static async Task KillDockerProcessGroupAsync(BackgroundJob job, long pgid, CancellationToken cancellationToken)
    {
        await RunDockerExecShellAsync(job.DockerBinary!, job.ContainerName!, $"kill -TERM -{pgid} 2>/dev/null || true", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await Task.Delay(KillGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (!job.HasExited)
        {
            await RunDockerExecShellAsync(job.DockerBinary!, job.ContainerName!, $"kill -KILL -{pgid} 2>/dev/null || true", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task RefreshDockerJobAsync(BackgroundJob job, CancellationToken cancellationToken)
    {
        // Observing exit first ensures the subsequent snapshot includes the command's final bytes.
        int? completedExitCode = null;
        if (job.DockerExitPath is { } exitPath)
        {
            byte[] exitBytes = await RunDockerExecCaptureBytesAsync(
                job.DockerBinary!, job.ContainerName!, $"cat {exitPath} 2>/dev/null || true",
                cancellationToken).ConfigureAwait(false);
            if (int.TryParse(Encoding.UTF8.GetString(exitBytes).Trim(), out int exitCode))
            {
                completedExitCode = exitCode;
            }
        }

        if (job.DockerOutputPath is { } outputPath)
        {
            byte[] output = await RunDockerExecCaptureBytesAsync(
                job.DockerBinary!,
                job.ContainerName!,
                $"cat {outputPath} 2>/dev/null || true",
                cancellationToken).ConfigureAwait(false);
            int headerEnd = Array.IndexOf(output, (byte)'\n');
            if (headerEnd >= 0 && long.TryParse(Encoding.ASCII.GetString(output, 0, headerEnd), out long totalBytes))
            {
                job.AppendDockerOutputSnapshot(output[(headerEnd + 1)..], totalBytes);
            }
        }

        if (completedExitCode is { } completed)
        {
            job.MarkExited(completed);
        }
    }

    private static async Task<(int ExitCode, string Error)> RunDockerExecShellAsync(string dockerBinary, string containerName, string script, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dockerBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(containerName);
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            _ = process.Start();
            Task copyOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await copyOutput.ConfigureAwait(false);
            return (process.ExitCode, await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }
    }

    private static async Task<byte[]> RunDockerExecCaptureBytesAsync(
        string dockerBinary,
        string containerName,
        string script,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dockerBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(containerName);
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            using var output = new MemoryStream();
            Task copyOutput = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            Task copyError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(copyOutput, copyError).ConfigureAwait(false);
            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
        {
        }
    }

    private sealed class BackgroundJob(
        string jobId,
        Process process,
        string? containerName,
        string? dockerBinary)
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource<long> _pgidReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;
        private bool _exited;
        private bool _killed;
        private int? _exitCode;
        private long? _processGroupId;

        public string JobId { get; } = jobId;
        public Process Process { get; } = process;
        public string? ContainerName { get; } = containerName;
        public string? DockerBinary { get; } = dockerBinary;
        public BoundedOutputBuffer Buffer { get; set; } = new();
        public string? DockerOutputPath { get; init; }
        public string? DockerExitPath { get; init; }

        private long _dockerOutputBytesRead;

        public bool HasExited
        {
            get
            {
                lock (_gate)
                {
                    return _exited;
                }
            }
        }

        public void AppendLine(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            Buffer.Append(bytes);
        }

        public void AppendDockerOutputSnapshot(byte[] output, long totalBytes)
        {
            if (totalBytes <= 0)
            {
                return;
            }

            lock (_gate)
            {
                if (totalBytes <= _dockerOutputBytesRead)
                {
                    return;
                }

                long unreadBytes = totalBytes - _dockerOutputBytesRead;
                long missingBytes = unreadBytes - output.LongLength;
                if (missingBytes > 0)
                {
                    Buffer.AppendGap(missingBytes);
                }

                if (unreadBytes < output.LongLength)
                {
                    int offset = output.Length - (int)unreadBytes;
                    Buffer.Append(output.AsSpan(offset));
                }
                else
                {
                    Buffer.Append(output);
                }

                _dockerOutputBytesRead = totalBytes;
            }
        }

        public bool TryCapturePgid(string line)
        {
            if (!line.StartsWith("PGID:", StringComparison.Ordinal))
            {
                return false;
            }

            if (!long.TryParse(line.AsSpan("PGID:".Length), out long pgid))
            {
                return false;
            }

            SetProcessGroupId(pgid);
            return true;
        }

        public void SetProcessGroupId(long processGroupId)
        {
            lock (_gate)
            {
                _processGroupId = processGroupId;
            }

            _ = _pgidReady.TrySetResult(processGroupId);
        }

        public async Task<long?> WaitForPgidAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_processGroupId is { } pgid)
                {
                    return pgid;
                }
            }

            try
            {
                return await _pgidReady.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        public void MarkKilled()
        {
            lock (_gate)
            {
                _killed = true;
            }
        }

        public void MarkExited(int? exitCode)
        {
            lock (_gate)
            {
                _exited = true;
                if (exitCode is not null || _exitCode is null)
                {
                    _exitCode = exitCode;
                }
            }
            Completion.TrySetResult();
        }

        public (string Status, int? ExitCode) StatusSnapshot()
        {
            lock (_gate)
            {
                if (_exited)
                {
                    return (_killed ? "killed" : "exited", _exitCode);
                }

                return (_killed ? "killing" : "running", null);
            }
        }

        public void DisposeProcess()
        {
            lock (_gate) { if (_disposed) { return; } _disposed = true; }
            Process.Dispose();
        }

        public void KillHostProcess()
        {
            KillProcessTree(Process);
        }
    }

    /// <summary>Releases output only when its owning handle is discarded.</summary>
    private async Task CleanupAsync(BackgroundJob job)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string output = job.DockerOutputPath!, exit = job.DockerExitPath!;
            var result = await RunDockerExecShellAsync(job.DockerBinary!, job.ContainerName!,
                $"rm -f -- {QuotePosix(output)} {QuotePosix(output + ".tmp")} {QuotePosix(output + ".chunk")} " +
                $"{QuotePosix(exit)} {QuotePosix(exit + ".status")}", timeout.Token).ConfigureAwait(false);
            // Session eviction may remove the container before its handles are discarded.
            if (result.ExitCode != 0)
            {
                logger.LogDebug("Could not remove output for background job {JobId} in container {ContainerName}: {Error}",
                    job.JobId, job.ContainerName, result.Error);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "Could not remove output for background job {JobId} in container {ContainerName}.",
                job.JobId, job.ContainerName);
        }
    }

    private sealed class Handle(BackgroundJob job, DockerBackgroundJobs owner) : IExecutionBackgroundJob
    {
        public Task Completion => job.Completion.Task;
        public bool HasExited => job.HasExited;
        public int? ExitCode => job.StatusSnapshot().ExitCode;
        public Task RefreshAsync(CancellationToken ct) => RefreshDockerJobAsync(job, ct);
        public Task StopAsync(CancellationToken ct) => StopDockerJobAsync(job, ct);
        public async ValueTask DisposeAsync()
        {
            if (!HasExited) { await StopDockerJobAsync(job, CancellationToken.None).ConfigureAwait(false); }
            job.KillHostProcess();
            job.DisposeProcess();
            job.MarkExited(null);
            await owner.CleanupAsync(job).ConfigureAwait(false);
        }
    }
}
