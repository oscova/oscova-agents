using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oscova.Agents.Core.Cli;

namespace Oscova.Agents.App.Cli;

/// <summary>Controls one local enrollment through a private pipe even when its server is unreachable.</summary>
internal sealed class LocalNodeControl : IAsyncDisposable
{
    private readonly FileStream _lease;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _listener;
    private readonly string _pipeName;
    private string _state = "Connecting";

    private LocalNodeControl(FileStream lease, string pipeName, CancellationTokenSource runtime)
    {
        _lease = lease;
        _pipeName = pipeName;
        _listener = ListenAsync(runtime);
    }

    /// <summary>Claims the enrollment for this process without competing with the server lifecycle.</summary>
    public static LocalNodeControl Start(CliPaths paths, CliNodeEntry entry, CancellationTokenSource runtime)
    {
        var name = GetPipeName(paths, entry);
        var lease = AvatarFileLease.TryAcquire(Path.Combine(LocalLifecycleLease.GetDirectory(paths.RootDirectory), name + ".lock"))
            ?? throw new CliExitException(1, "This node is already running or starting. Use 'oscova node status'.");
        return new(lease, name, runtime);
    }

    /// <summary>Publishes connection state without retaining credentials or user content.</summary>
    public void SetState(string state) => Volatile.Write(ref _state, state);

    /// <summary>Queries or stops only the same OS user's selected enrollment.</summary>
    public static async Task<NodeProcessStatus?> RequestAsync(CliPaths paths, CliNodeEntry entry, bool stop, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", GetPipeName(paths, entry), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }

        await pipe.WriteAsync(new byte[] { stop ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe);
        var json = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NodeProcessStatus>(json ?? throw new IOException("The node closed its control connection."), CliJson.Options);
    }

    private async Task ListenAsync(CancellationTokenSource runtime)
    {
        using var process = Process.GetCurrentProcess();
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                request.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var command = new byte[1];
                    await pipe.ReadExactlyAsync(command, request.Token).ConfigureAwait(false);
                    if (command[0] > 1) { continue; }

                    var status = new NodeProcessStatus(Volatile.Read(ref _state), process.Id, process.StartTime.ToUniversalTime());
                    await using var writer = new StreamWriter(pipe, leaveOpen: true);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(status).AsMemory(), request.Token).ConfigureAwait(false);
                    await writer.FlushAsync(request.Token).ConfigureAwait(false);
                    if (command[0] == 1)
                    {
                        await runtime.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // A disconnected local client does not stop the node.
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private static string GetPipeName(CliPaths paths, CliNodeEntry entry)
    {
        var root = Path.GetFullPath(paths.RootDirectory);
        if (OperatingSystem.IsWindows()) { root = root.ToUpperInvariant(); }
        // Keep the full enrollment hash within macOS's default temporary-directory socket limit.
        return "oscova-n-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            root + "\n" + entry.Host + "\n" + entry.UserId + "\n" + entry.NodeId)))[..32];
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _listener.ConfigureAwait(false); }
        finally { _lease.Dispose(); _lifetime.Dispose(); }
    }
}

/// <summary>Identifies the live local node and its current connection state.</summary>
internal sealed record NodeProcessStatus(string State, int Pid, DateTime StartedAtUtc);
