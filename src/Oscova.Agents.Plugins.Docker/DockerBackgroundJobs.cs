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
        string outputCountPath = $"/tmp/oscova-bg-{jobId}.count";
        string exitPath = $"/tmp/oscova-bg-{jobId}.exit";
        // Keep Docker background output available after the host docker-exec carrier exits,
        // but cap the container-side file so a noisy watcher cannot fill /tmp.
        string wrapper =
            $"LIMIT={BoundedOutputBuffer.DefaultCapacityBytes}; TOTAL=0; STATUS_FILE={QuotePosix(exitPath + ".status")}; " +
            $"rm -f {outputPath} {outputPath}.tmp {outputCountPath} {exitPath} \"$STATUS_FILE\"; " +
            "echo \"PGID:$$\"; " +
            $"{{ bash -lc {QuotePosix(command)}; printf '%s' \"$?\" > \"$STATUS_FILE\"; }} 2>&1 | " +
            "while IFS= read -r LINE || [ -n \"$LINE\" ]; do " +
            $"printf '%s\\n' \"$LINE\" >> {outputPath}; " +
            "BYTES=$(printf '%s\\n' \"$LINE\" | wc -c); " +
            "TOTAL=$((TOTAL + BYTES)); " +
            $"printf '%s' \"$TOTAL\" > {outputCountPath}; " +
            $"SIZE=$(wc -c < {outputPath} 2>/dev/null || printf '0'); " +
            $"if [ \"$SIZE\" -gt \"$LIMIT\" ]; then tail -c \"$LIMIT\" {outputPath} > {outputPath}.tmp && mv {outputPath}.tmp {outputPath}; fi; " +
            "done; " +
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
            DockerOutputCountPath = outputCountPath,
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
            throw new ToolFailureException("The Docker background command could not be confirmed.", "Check the sandbox and try again.");
        }

        return new Handle(job);
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
        if (job.DockerOutputPath is { } outputPath)
        {
            byte[] output = await RunDockerExecCaptureBytesAsync(
                job.DockerBinary!,
                job.ContainerName!,
                $"cat {outputPath} 2>/dev/null || true",
                cancellationToken).ConfigureAwait(false);
            long totalBytes = output.LongLength;
            if (job.DockerOutputCountPath is { } outputCountPath)
            {
                byte[] countBytes = await RunDockerExecCaptureBytesAsync(
                    job.DockerBinary!,
                    job.ContainerName!,
                    $"cat {outputCountPath} 2>/dev/null || true",
                    cancellationToken).ConfigureAwait(false);
                if (long.TryParse(Encoding.UTF8.GetString(countBytes).Trim(), out long parsedBytes))
                {
                    totalBytes = Math.Max(totalBytes, parsedBytes);
                }
            }

            job.AppendDockerOutputSnapshot(output, totalBytes);
        }

        if (job.DockerExitPath is { } exitPath)
        {
            byte[] exitBytes = await RunDockerExecCaptureBytesAsync(
                job.DockerBinary!,
                job.ContainerName!,
                $"cat {exitPath} 2>/dev/null || true",
                cancellationToken).ConfigureAwait(false);
            string exitText = Encoding.UTF8.GetString(exitBytes).Trim();
            if (int.TryParse(exitText, out int exitCode))
            {
                job.MarkExited(exitCode);
            }
        }
    }

    private static async Task RunDockerExecShellAsync(string dockerBinary, string containerName, string script, CancellationToken cancellationToken)
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
            Task copyError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(copyOutput, copyError).ConfigureAwait(false);
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
        public string? DockerOutputCountPath { get; init; }
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

    private sealed class Handle(BackgroundJob job) : IExecutionBackgroundJob
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
        }
    }
}
