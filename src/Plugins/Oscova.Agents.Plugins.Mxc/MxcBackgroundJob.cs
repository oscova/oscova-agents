using System.Text;
using Microsoft.Mxc.Sdk.V1;
using Oscova.Agents.Execution;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Plugins.Mxc;

/// <summary>Drains an independent native process before completing the host's background-job ownership.</summary>
internal sealed class MxcBackgroundJob : IExecutionBackgroundJob
{
    private readonly MxcProcess _process;
    private readonly Stream _input;
    private readonly Task _output;
    private readonly Task _error;
    private readonly Lock _gate = new();
    private Task? _stop;
    public Task Completion { get; }
    public bool HasExited => Completion.IsCompleted;
    public int? ExitCode { get; private set; }

    public MxcBackgroundJob(MxcProcess process, BoundedOutputBuffer buffer)
    {
        _process = process;
        _input = process.StandardInput ?? throw new IOException("MXC did not provide command input.");
        _output = DrainAsync(process.StandardOutput, buffer);
        _error = DrainAsync(process.StandardError, buffer);
        Completion = CompleteAsync();
    }

    public async Task StartAsync(string command, CancellationToken ct)
    {
        var script = OperatingSystem.IsWindows()
            ? ". ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
              Convert.ToBase64String(Encoding.UTF8.GetBytes(command)) + "')))); if ($?) { exit $LASTEXITCODE } else { exit 1 }\n"
            : command + "\nexit $?\n";
        await _input.WriteAsync(Encoding.UTF8.GetBytes(script), ct).ConfigureAwait(false);
        await _input.FlushAsync(ct).ConfigureAwait(false);
        _input.Dispose();
    }

    private static Task DrainAsync(Stream? stream, BoundedOutputBuffer buffer) => Task.Run(async () =>
    {
        if (stream is null) { return; }
        using (stream)
        {
            var bytes = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(bytes).ConfigureAwait(false)) > 0) { buffer.Append(bytes.AsSpan(0, count)); }
        }
    });

    private async Task CompleteAsync()
    {
        try
        {
            var result = await _process.WaitAsync().ConfigureAwait(false);
            ExitCode = result.ExitCode;
        }
        finally
        {
            try
            {
                // End descendants before draining, while keeping caller-owned streams open.
                _process.Kill();
                await Task.WhenAll(_output, _error).ConfigureAwait(false);
            }
            finally { _process.Dispose(); }
        }
    }

    public Task RefreshAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    public Task StopAsync(CancellationToken ct)
    {
        lock (_gate) { return _stop ??= StopCoreAsync(); }
    }

    private async Task StopCoreAsync()
    {
        if (!Completion.IsCompleted)
        {
            try { _process.Kill(); }
            // Completion owns disposal and may be finishing its finally block concurrently.
            catch (ObjectDisposedException) { }
        }
        await Completion.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
}
