using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;
using Microsoft.Agents.AI.Tools.Shell;
using Oscova.Agents.Execution;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>Supplies the platform shell as the built-in execution environment.</summary>
public sealed class HostExecutionProvider : IExecutionEnvironmentProvider
{
    /// <inheritdoc />
    public Task<ExecutionAvailabilityResult> CheckAvailabilityAsync(ExecutionAvailabilityRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ExecutionAvailabilityResult(true));
    }

    public static ExecutionEnvironmentDefinition Definition { get; } = new("host", "Host",
        "Commands run directly on this device with its operating-system account's permissions.", true, false, true, false,
        RuntimeInformation.OSDescription, OperatingSystem.IsWindows() ? "PowerShell" : "sh");
    /// <inheritdoc />
    public async Task<IExecutionSession> CreateSessionAsync(ExecutionSessionRequest request, CancellationToken ct)
    {
        var executor = new LocalShellExecutor(new LocalShellExecutorOptions
        {
            Mode = ShellMode.Persistent, WorkingDirectory = request.WorkingDirectory, ConfineWorkingDirectory = true,
            CleanEnvironment = true, Environment = request.Environment, Policy = request.Policy,
            MaxOutputBytes = request.MaxOutputBytes, Timeout = request.Timeout
        });
        try { await executor.InitializeAsync(ct).ConfigureAwait(false); return new Session(executor, request); }
        catch { await executor.DisposeAsync().ConfigureAwait(false); throw; }
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    /// <inheritdoc />
    public Task StopAsync() => Task.CompletedTask;
    private sealed class Session(LocalShellExecutor executor, ExecutionSessionRequest request) : IExecutionSession
    {
        public ShellExecutor Executor => executor;
        public string? ShellBinary => executor.ResolvedShellBinary;
        public string ResolveScriptPath(string hostPath) => Path.GetFullPath(hostPath);
        public Task<bool> RecoverForegroundAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<IExecutionBackgroundJob> StartBackgroundAsync(string command, BoundedOutputBuffer buffer, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult<IExecutionBackgroundJob>(new Job(BuildLocalStartInfo(command, request.WorkingDirectory, ShellBinary, request.Environment), buffer)); }
        public ValueTask DisposeAsync() => executor.DisposeAsync();
    }
    private sealed class Job : IExecutionBackgroundJob
    {
        private readonly Process _process;
        private readonly int? _processGroup;
        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }
        public Task Completion { get; }
        public Job(ProcessStartInfo info, BoundedOutputBuffer buffer)
        {
            _process = new() { StartInfo = info };
            try { _process.Start(); }
            catch { _process.Dispose(); throw; }

            if (!OperatingSystem.IsWindows() && Path.GetFileName(info.FileName) == "setsid")
            {
                _processGroup = _process.Id;
            }

            Completion = CompleteAsync(Task.WhenAll(
                PumpAsync(_process.StandardOutput, buffer),
                PumpAsync(_process.StandardError, buffer)));
        }
        private static async Task PumpAsync(StreamReader reader, BoundedOutputBuffer buffer)
        {
            // A flushed readiness marker must be visible before its newline or process exit.
            var characters = new char[4096];
            var bytes = new byte[Encoding.UTF8.GetMaxByteCount(characters.Length)];
            var encoder = Encoding.UTF8.GetEncoder();
            int count;
            while ((count = await reader.ReadAsync(characters).ConfigureAwait(false)) > 0)
            {
                // Each pipe retains incomplete characters before complete UTF-8 sequences are merged.
                var byteCount = encoder.GetBytes(characters.AsSpan(0, count), bytes, flush: false);
                buffer.Append(bytes.AsSpan(0, byteCount));
            }

            buffer.Append(bytes.AsSpan(0, encoder.GetBytes([], bytes, flush: true)));
        }
        private async Task CompleteAsync(Task output)
        {
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                ExitCode = _process.ExitCode;
                await output.ConfigureAwait(false);
                // A launcher may exit while its reparented watcher still owns permission and configuration leases.
                while (_processGroup is { } group && HasLiveGroupMember(group))
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }

                HasExited = true;
            }
            finally { _process.Dispose(); }
        }
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task StopAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!HasExited)
            {
                if (_processGroup is { } group && Kill(-group, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not stop the background process group.");
                }

                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }

            await Completion.WaitAsync(ct).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

        private static bool HasLiveGroupMember(int group)
        {
            // kill(group, 0) includes zombies, which can persist under a container's PID 1 after all work stops.
            var processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    if (GetProcessGroup(process.Id) == group && !process.HasExited) { return true; }
                }

                return false;
            }
            finally
            {
                foreach (var process in processes) { process.Dispose(); }
            }
        }

        [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
        private static extern int GetProcessGroup(int pid);

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int Kill(int pid, int signal);
    }
    private static ProcessStartInfo BuildLocalStartInfo(string command, string workingDirectory, string? shellBinary, IReadOnlyDictionary<string, string?> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        startInfo.Environment.Clear();
        foreach (var entry in environment)
        {
            if (entry.Value is not null)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            string shell = string.IsNullOrWhiteSpace(shellBinary)
                ? ExecutableLocator.FindOnPath("pwsh") ?? ExecutableLocator.FindOnPath("powershell") ?? "cmd.exe"
                : shellBinary;
            startInfo.FileName = shell;
            if (Path.GetFileNameWithoutExtension(shell).Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
            }
            else
            {
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                // One-shot processes must honor the exit status preserved by per-call cwd framing.
                startInfo.ArgumentList.Add(command + "\nif (-not $?) { exit 1 }\nexit $LASTEXITCODE");
            }

            return startInfo;
        }

        string posixShell = string.IsNullOrWhiteSpace(shellBinary)
            ? File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh"
            : shellBinary;
        string? setsid = ExecutableLocator.FindOnPath("setsid") ?? (File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : null);
        if (setsid is not null)
        {
            startInfo.FileName = setsid;
            startInfo.ArgumentList.Add(posixShell);
        }
        else
        {
            startInfo.FileName = posixShell;
        }

        if (Path.GetFileName(posixShell).Equals("bash", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("--noprofile");
            startInfo.ArgumentList.Add("--norc");
        }

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

}
