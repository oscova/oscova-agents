using System.Buffers.Binary;
using Oscova.Agents.Core.ApiContracts.Updates;
using System.IO.Pipes;
using System.Text.Json;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Host.Hosting;

namespace Oscova.Agents.App.Cli;

/// <summary>Relays startup progress and onboarding between a detached child and its terminal parent.</summary>
internal sealed class CliStartupPipe(PipeStream stream) : IDisposable
{
    private const int MaximumMessageBytes = 64 * 1024;
    private const string NamePrefix = "oscova-";
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<bool> _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<IReadOnlyList<StartupPluginSelection>>? _pluginSelection;
    private IReadOnlyList<StartupPluginOption>? _offeredPlugins;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _choiceRequested;
    private bool _ready;
    private Task? _observer;
    private bool _disposed;

    public CancellationToken ParentDisconnected => _lifetime.Token;
    public bool Interactive { get; private set; }

    /// <summary>Creates a private server before the child process launches.</summary>
    public static CliStartupPipe CreateServer(out string name)
    {
        // Keep the full random identity within macOS's socket limit under its default temporary directory.
        name = NamePrefix + Guid.NewGuid().ToString("N");
        return new(new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
    }

    /// <summary>Connects the child and begins observing parent disconnection throughout preparation.</summary>
    public static async Task<CliStartupPipe> ConnectChildAsync(string name, CancellationToken ct)
    {
        if (!name.StartsWith(NamePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[NamePrefix.Length..], "N", out _))
        {
            throw new CliExitException(4, "The startup pipe name is invalid.");
        }

        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var pipe = new CliStartupPipe(client);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(connect.Token).ConfigureAwait(false);
            var hello = await pipe.ReadAsync(connect.Token).ConfigureAwait(false);
            if (hello.Kind != StartupMessageKind.Hello) { throw new IOException("Unexpected startup handshake."); }

            pipe.Interactive = hello.Choice;
            pipe._observer = pipe.ObserveParentAsync();
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    /// <summary>Waits for the launched child to connect and sends the parent's prompt policy.</summary>
    public async Task AcceptAsync(bool interactive, CancellationToken ct)
    {
        await ((NamedPipeServerStream)stream).WaitForConnectionAsync(ct).ConfigureAwait(false);
        Interactive = interactive;
        await SendAsync(new(StartupMessageKind.Hello, Choice: interactive), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one bounded frame, serializing progress callbacks without interleaving bytes.
    /// A non-interactive child keeps starting after its terminal closes, so its sends become no-ops.
    /// </summary>
    public async Task SendAsync(StartupMessage message, CancellationToken ct)
    {
        if (SurvivesParent && _lifetime.IsCancellationRequested) { return; }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, CliJson.Options);
        if (bytes.Length > MaximumMessageBytes) { throw new IOException("Startup message exceeds 64 KiB."); }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) when (SurvivesParent)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    // Only the child observes its parent, and only a child without prompts can finish alone.
    private bool SurvivesParent => _observer is not null && !Interactive;

    /// <summary>Reads a bounded frame and rejects malformed startup messages.</summary>
    public async Task<StartupMessage> ReadAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes) { throw new IOException("Invalid startup message size."); }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<StartupMessage>(bytes, CliJson.Options);
        if (message is null || !Enum.IsDefined(message.Kind)) { throw new IOException("Invalid startup message."); }

        if (message.Kind == StartupMessageKind.Failed) { throw new CliExitException(1, message.Text ?? "Oscova startup failed."); }

        return message;
    }

    /// <summary>Requests the parent's optional Avatar choice while the child retains startup ownership.</summary>
    public async Task<bool> RequestAvatarChoiceAsync(CancellationToken ct)
    {
        _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _choiceRequested = true;
        await SendAsync(new(StartupMessageKind.AvatarChoice), ct).ConfigureAwait(false);
        return await _choice.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Waits for the entire checklist and edition flow before submitting optional setup choices.</summary>
    public async Task<IReadOnlyList<StartupPluginSelection>> RequestPluginSelectionAsync(
        IReadOnlyList<StartupPluginOption> offered, CancellationToken ct)
    {
        _offeredPlugins = offered;
        _pluginSelection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync(new(StartupMessageKind.PluginSelection, OfferedPlugins: offered), ct).ConfigureAwait(false);
        return await _pluginSelection.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Transfers the ready child to independent operation after the parent verifies its descriptor.</summary>
    public async Task ReleaseWhenReadyAsync(CancellationToken ct)
    {
        _ready = true;
        await SendAsync(new(StartupMessageKind.Ready), ct).ConfigureAwait(false);
        try
        {
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (SurvivesParent && !ct.IsCancellationRequested)
        {
            // The terminal closed after startup, the ready Host keeps running.
        }

        stream.Dispose();
    }

    private async Task ObserveParentAsync()
    {
        try
        {
            while (true)
            {
                var message = await ReadAsync(_lifetime.Token).ConfigureAwait(false);
                if (message.Kind == StartupMessageKind.AvatarAnswer && _choiceRequested && !_choice.Task.IsCompleted)
                {
                    _choice.TrySetResult(message.Choice);
                }
                else if (message.Kind == StartupMessageKind.PluginSelectionAnswer && _pluginSelection is { Task.IsCompleted: false } && _offeredPlugins is not null)
                {
                    var selected = message.SelectedPlugins ?? throw new InvalidDataException("Startup plugin selections are required.");
                    StartupPluginSelection.Validate(_offeredPlugins, selected);
                    _pluginSelection.TrySetResult(selected);
                }
                else if (message.Kind == StartupMessageKind.Release && _ready)
                {
                    _release.TrySetResult();
                    return;
                }
                else { throw new IOException("Unexpected startup response."); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or CliExitException)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _choice.TrySetCanceled();
            _pluginSelection?.TrySetCanceled();
            _release.TrySetCanceled();
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }

        _disposed = true;
        _lifetime.Cancel();
        stream.Dispose();
        _observer?.GetAwaiter().GetResult();
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}

internal enum StartupMessageKind { Hello, Version, ComponentsBegin, Component, ComponentsEnd, PluginSelection, PluginSelectionAnswer, PluginResults, AvatarChoice, AvatarAnswer, AvatarBegin, AvatarProgress, AvatarEnd, AvatarPlatformNotice, Notice, Prepared, Ready, Release, Failed }

/// <summary>Carries startup display data and the parent's completed setup choices.</summary>
internal sealed record StartupMessage(
    StartupMessageKind Kind,
    string? Text = null,
    DefaultPluginInstallProgress? Component = null,
    AvatarDownloadProgress? Avatar = null,
    bool Choice = false,
    IReadOnlyList<StartupPluginOption>? OfferedPlugins = null,
    IReadOnlyList<StartupPluginSelection>? SelectedPlugins = null);
