using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Microsoft.Mxc.Sdk.V1;
using Oscova.Agents.Execution;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Plugins.Mxc;

/// <summary>Keeps shell variables alive over MXC streams while bounding each command's output and lifetime.</summary>
internal sealed class MxcShellExecutor(MxcRuntime runtime, ExecutionSessionRequest request) : ShellExecutor
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private MxcProcess? _process;
    private Stream? _input;
    private Stream? _output;
    private Stream? _error;
    private readonly Lock _disposeGate = new();
    private Task? _disposal;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await StartAsync(linked.Token).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        if (_process is not null) { return; }
        _process = await runtime.SpawnAsync(ct).ConfigureAwait(false);
        // MXC transfers each standard stream only once.
        _input = _process.StandardInput ?? throw new IOException("MXC did not provide shell input.");
        _output = _process.StandardOutput ?? throw new IOException("MXC did not provide shell output.");
        _error = _process.StandardError ?? throw new IOException("MXC did not provide shell error output.");
    }

    public override async Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        var decision = request.Policy.Evaluate(new ShellRequest(command, request.WorkingDirectory));
        if (!decision.Allowed) { throw new ShellCommandRejectedException($"Command rejected by policy: {decision.Reason}"); }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _commands.WaitAsync(lifetime.Token).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var stdout = new BoundedOutputBuffer(Math.Max(1, request.MaxOutputBytes / 2));
        var stderr = new BoundedOutputBuffer(Math.Max(1, request.MaxOutputBytes / 2));
        Task<int>? output = null;
        Task<int>? error = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(request.Timeout);
        try
        {
            await StartAsync(deadline.Token).ConfigureAwait(false);
            var tag = "OSCOVA_" + Guid.NewGuid().ToString("N") + ":";
            var outputStream = _output!;
            var errorStream = _error!;
            output = Task.Run(() => ReadFrameAsync(outputStream, tag, stdout), CancellationToken.None);
            error = Task.Run(() => ReadFrameAsync(errorStream, tag, stderr), CancellationToken.None);
            await _input!.WriteAsync(Encoding.UTF8.GetBytes(Frame(command, request.WorkingDirectory, tag)), deadline.Token).ConfigureAwait(false);
            await _input.FlushAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(deadline.Token).ConfigureAwait(false);
            return Result(await output.ConfigureAwait(false), timedOut: false);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            await CloseProcessAsync().ConfigureAwait(false);
            await DrainReadersAsync(output, error).ConfigureAwait(false);
            return Result(-1, timedOut: true);
        }
        catch (Exception ex)
        {
            var exitCode = _process?.TryGetExitCode(out var code) == true ? code.ToString(CultureInfo.InvariantCulture) : "unknown";
            await CloseProcessAsync().ConfigureAwait(false);
            await DrainReadersAsync(output, error).ConfigureAwait(false);
            if (ex is EndOfStreamException)
            {
                throw new IOException($"The MXC shell exited before completing the command (exit {exitCode}). {stderr.ReadFrom(0, request.MaxOutputBytes).Text}", ex);
            }
            throw;
        }
        finally { _commands.Release(); }

        ShellResult Result(int exitCode, bool timedOut) => new(
            stdout.ReadFrom(0, request.MaxOutputBytes).Text, stderr.ReadFrom(0, request.MaxOutputBytes).Text,
            exitCode, stopwatch.Elapsed, stdout.Truncated || stderr.Truncated, timedOut);
    }

    private static string Frame(string command, string directory, string tag)
    {
        if (OperatingSystem.IsWindows())
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Set-Location -ErrorAction Stop -LiteralPath '" +
                directory.Replace("'", "''", StringComparison.Ordinal) + "'\n" + command));
            // Flush formatted output before markers while dot-sourcing preserves the shell's variables.
            return "$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $global:LASTEXITCODE=$null; $__oscovaCode=0; try { " +
                ". ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded +
                "')))) 2>&1 | ForEach-Object { if ($_ -is [Management.Automation.ErrorRecord]) { [Console]::Error.WriteLine(($_ | Out-String).TrimEnd()); $__oscovaCode=1 } " +
                "else { [Console]::Out.WriteLine(($_ | Out-String).TrimEnd()) } }; " +
                "if ($null -ne $global:LASTEXITCODE) { $__oscovaCode=$global:LASTEXITCODE } } " +
                "catch { [Console]::Error.WriteLine($_); $__oscovaCode=1 }; [Console]::Out.WriteLine('" + tag +
                "'+$__oscovaCode); [Console]::Error.WriteLine('" + tag + "'+$__oscovaCode)\n";
        }
        return "builtin cd -- '" + Quote(directory) + "' && builtin eval '" + Quote(command) +
            "'\n__oscova_code=$?; builtin printf '" + tag + "%s\\n' \"$__oscova_code\"; builtin printf '" + tag + "%s\\n' \"$__oscova_code\" >&2\n";
    }

    private static string Quote(string value) => value.Replace("'", "'\\''", StringComparison.Ordinal);

    // Retain only a possible marker suffix, even when the command emits an unbounded line.
    private static async Task<int> ReadFrameAsync(Stream stream, string tag, BoundedOutputBuffer output)
    {
        var marker = Encoding.ASCII.GetBytes(tag);
        var buffer = new byte[4096 + marker.Length + 16];
        var retained = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(retained, 4096)).ConfigureAwait(false);
            if (count == 0)
            {
                output.Append(buffer.AsSpan(0, retained));
                throw new EndOfStreamException("The MXC shell exited before completing the command.");
            }
            var length = retained + count;
            var start = buffer.AsSpan(0, length).IndexOf(marker);
            if (start >= 0)
            {
                output.Append(buffer.AsSpan(0, start));
                var statusStart = start + marker.Length;
                var end = buffer.AsSpan(statusStart, length - statusStart).IndexOf((byte)'\n');
                if (end >= 0)
                {
                    if (int.TryParse(Encoding.ASCII.GetString(buffer, statusStart, end).Trim(), CultureInfo.InvariantCulture, out var status)) { return status; }
                    throw new IOException("The MXC shell returned an invalid completion marker.");
                }
                retained = length - start;
                if (retained > marker.Length + 12) { throw new IOException("The MXC shell returned an invalid completion marker."); }
                buffer.AsSpan(start, retained).CopyTo(buffer);
            }
            else
            {
                retained = Math.Min(marker.Length - 1, length);
                output.Append(buffer.AsSpan(0, length - retained));
                buffer.AsSpan(length - retained, retained).CopyTo(buffer);
            }
        }
    }

    private static async Task DrainReadersAsync(Task<int>? output, Task<int>? error)
    {
        foreach (var task in new[] { output, error })
        {
            if (task is null) { continue; }
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    private async Task CloseProcessAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) { return; }
        // Dispose kills and waits for the native process tree before releasing its policy.
        await Task.Run(process.Dispose).ConfigureAwait(false);
        _input?.Dispose();
        _output?.Dispose();
        _error?.Dispose();
        _input = _output = _error = null;
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try { await CloseProcessAsync().ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true)
    {
        var function = AIFunctionFactory.Create(
            async ([Description("The shell command to execute.")] string command, CancellationToken ct) =>
                await RunAsync(command, ct).ConfigureAwait(false),
            new AIFunctionFactoryOptions { Name = name, Description = description ?? "Runs a command in the selected execution environment." });
        return requireApproval ? new ApprovalRequiredAIFunction(function) : function;
    }

    public override ValueTask DisposeAsync()
    {
        lock (_disposeGate) { return new(_disposal ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _commands.WaitAsync().ConfigureAwait(false);
        try { await CloseProcessAsync().ConfigureAwait(false); }
        finally { _commands.Release(); }
    }
}
