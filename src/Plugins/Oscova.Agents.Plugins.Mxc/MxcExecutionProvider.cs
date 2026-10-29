using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Mxc.Sdk.V1;
using Oscova.Agents.Execution;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Plugins.Mxc;

/// <summary>Supplies native sandbox sessions and checks prerequisites on the executing device.</summary>
public sealed class MxcExecutionProvider : IExecutionEnvironmentProvider
{
    public const string Id = "mxc";
    public static ExecutionEnvironmentDefinition Definition { get; } = new(Id, "Microsoft MXC",
        "Commands follow your workspace and network permissions using native operating-system isolation.",
        true, true, true, false, OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
        OperatingSystem.IsWindows() ? "PowerShell" : "bash", "Execution.Title", "Execution.Description", "not available", "not available");

    private readonly string _privateRoot;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _preparation = new(1, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource _stopping = new();
    private Task? _stop;
    private bool _disposed;

    /// <summary>Creates a device runtime without loading or probing native dependencies.</summary>
    public MxcExecutionProvider(AgentPluginContext context)
    {
        _privateRoot = context.PrivateDataDirectory ?? throw new InvalidOperationException("MXC requires private device storage.");
        _logger = context.LoggerFactory.CreateLogger<MxcExecutionProvider>();
    }

    /// <inheritdoc />
    public Task<ExecutionAvailabilityResult> CheckAvailabilityAsync(ExecutionAvailabilityRequest request, CancellationToken ct)
        => PrepareAsync<ExecutionAvailabilityResult>(async token =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var support = await Task.Run(MxcPlatform.GetPlatformSupport, CancellationToken.None).ConfigureAwait(false);
                var backend = OperatingSystem.IsWindows() ? ContainmentBackend.ProcessContainer
                    : OperatingSystem.IsMacOS() ? ContainmentBackend.Seatbelt : ContainmentBackend.Bubblewrap;
                if (!support.IsSupported || !support.AvailableMethods.Contains(backend))
                {
                    return new(false, $"{backend} was not detected. Check the Microsoft MXC prerequisites for this machine. {support.Reason}", "Availability.BackendUnavailable");
                }

                Directory.CreateDirectory(_privateRoot);
                var workspace = Directory.CreateDirectory(Path.Combine(_privateRoot, "check-" + Guid.NewGuid().ToString("N"))).FullName;
                try
                {
                    var sessionRequest = new ExecutionSessionRequest("check", "check", workspace,
                        [new(workspace, "Check", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)],
                        null, request.Environment, new ShellPolicy(), false, request.AllowNetwork, 4096, TimeSpan.FromSeconds(10));
                    await using var session = await CreateCoreAsync(sessionRequest, deadline.Token).ConfigureAwait(false);
                    var result = await session.Executor.RunAsync(OperatingSystem.IsWindows() ? "$null=0" : ":", deadline.Token).ConfigureAwait(false);
                    return result.ExitCode == 0 && !result.TimedOut ? new(true)
                        : new(false, $"Microsoft MXC could not start a shell. Check the prerequisites and system policy. {result.Stderr}", "Availability.LaunchFailed");
                }
                finally { Directory.Delete(workspace, recursive: true); }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new(false, "Microsoft MXC did not respond within ten seconds. Check the prerequisites and try again.", "Availability.Timeout");
            }
            catch (MxcException ex)
            {
                _logger.LogWarning(ex, "MXC prerequisite check failed with {Code}", ex.Code);
                return new(false, $"Microsoft MXC is unavailable. {ex.Message} {ex.Remediation}");
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException or IOException)
            {
                _logger.LogWarning(ex, "MXC prerequisite check failed");
                return new(false, $"Microsoft MXC could not start. Check its native dependencies and this machine's permissions. {ex.Message}", "Availability.LaunchFailed");
            }
        }, ct);

    /// <inheritdoc />
    public Task<IExecutionSession> CreateSessionAsync(ExecutionSessionRequest request, CancellationToken ct)
        => PrepareAsync(token => CreateCoreAsync(request, token), ct);

    private async Task<IExecutionSession> CreateCoreAsync(ExecutionSessionRequest request, CancellationToken ct)
    {
        var runtime = new MxcRuntime(request, _privateRoot, _logger);
        var executor = new MxcShellExecutor(runtime, request);
        try
        {
            await executor.InitializeAsync(ct).ConfigureAwait(false);
            return new Session(runtime, executor, request);
        }
        catch
        {
            try { await executor.DisposeAsync().ConfigureAwait(false); }
            finally { runtime.Dispose(); }
            throw;
        }
    }

    private async Task<T> PrepareAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stop is { IsCompleted: false }) { throw new OperationCanceledException("MXC preparation is stopping."); }
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        }
        using (linked)
        {
            await _preparation.WaitAsync(linked.Token).ConfigureAwait(false);
            try { return await operation(linked.Token).ConfigureAwait(false); }
            finally { _preparation.Release(); }
        }
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stop is { IsCompleted: false }) { return _stop; }
            _stopping.Cancel();
            return _stop = StopPreparationAsync();
        }
    }

    private async Task StopPreparationAsync()
    {
        await _preparation.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _stopping.Dispose();
                _stopping = new();
            }
        }
        finally { _preparation.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; }
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Owns the foreground and independent background processes sharing one immutable session policy.</summary>
    private sealed class Session(MxcRuntime runtime, MxcShellExecutor executor, ExecutionSessionRequest request) : IExecutionSession
    {
        private readonly SemaphoreSlim _creation = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<MxcBackgroundJob> _jobs = [];
        private readonly Lock _disposeGate = new();
        private Task? _disposal;
        public ShellExecutor Executor => executor;
        public string ShellBinary => MxcRuntime.Shell;

        public string ResolveScriptPath(string hostPath)
        {
            if (request.SkillsDirectory is { } skills && PathHelper.IsPathWithinDirectory(hostPath, skills))
            {
                return PathHelper.ResolveAuthorizedHostPath(hostPath,
                    [new(skills, "Skills", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)], WorkspacePathAccess.Read);
            }
            return PathHelper.ResolveAuthorizedHostPath(hostPath, request.Directories, WorkspacePathAccess.Read);
        }

        public async Task<bool> RecoverForegroundAsync(CancellationToken ct)
        {
            await executor.RecoverAsync(ct).ConfigureAwait(false);
            return true;
        }

        public async Task<IExecutionBackgroundJob> StartBackgroundAsync(string command, BoundedOutputBuffer buffer, CancellationToken ct)
        {
            var decision = request.Policy.Evaluate(new ShellRequest(command, request.WorkingDirectory));
            if (!decision.Allowed) { throw new ShellCommandRejectedException($"Command rejected by policy: {decision.Reason}"); }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            await _creation.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                _jobs.RemoveAll(job => job.HasExited);
                var process = await runtime.SpawnAsync(linked.Token).ConfigureAwait(false);
                MxcBackgroundJob job;
                try { job = new MxcBackgroundJob(process, buffer); }
                catch { process.Dispose(); throw; }
                _jobs.Add(job);
                try { await job.StartAsync(command, linked.Token).ConfigureAwait(false); }
                catch { await job.DisposeAsync().ConfigureAwait(false); throw; }
                return job;
            }
            finally { _creation.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate) { return new(_disposal ??= DisposeCoreAsync()); }
        }

        private async Task DisposeCoreAsync()
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _creation.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(_jobs.Select(job => job.DisposeAsync().AsTask()).Append(executor.DisposeAsync().AsTask())).ConfigureAwait(false);
            }
            finally { runtime.Dispose(); _creation.Release(); }
        }
    }
}
