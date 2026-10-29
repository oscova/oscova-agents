using Oscova.Agents.Execution;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.ApiContracts.Chat;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Harness;
using Oscova.Agents.Providers;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>
/// Produces and caches the shell executor owned by the active agent run.
/// </summary>
public sealed class ShellExecutorFactory : IShellExecutorProvider, IAsyncDisposable
{
    private readonly RunContextAccessor _runContextAccessor;
    private readonly IUserDirectoryService? _userDirectoryService;
    private readonly IExecutionEnvironmentCatalog _environments;
    private readonly Func<string, CancellationToken, Task<string?>> _resolveModeAsync;
    private readonly Func<bool> _allowSandboxNetworkAccess;
    private readonly ExecutionPermissionRegistry? _executions;
    private readonly ConcurrentDictionary<(string ExecutionId, string AgentId, string? Mode, bool FullAccess, string? Worker), CachedExecutor> _byExecutionId = new();

    /// <summary>Creates server executors under the account's current permissions and execution settings.</summary>
    public ShellExecutorFactory(
        RunContextAccessor runContextAccessor,
        IUserDirectoryService userDirectoryService,
        UserServiceManager userServiceManager,
        NetworkSettings networkSettings,
        IExecutionEnvironmentCatalog environments,
        ILogger<ShellExecutorFactory> logger,
        IUserPermissionService permissions, ExecutionPermissionRegistry executions)
        : this(runContextAccessor, userDirectoryService, logger, environments,
            async (userId, ct) =>
            {
                var run = runContextAccessor.Current
                    ?? throw new InvalidOperationException("Shell execution requires an active run.");
                var scope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
                await ConversationExecutionAccess.RequireAsync(permissions, userId, run.PermissionPreset, ct).ConfigureAwait(false);
                foreach (var key in UserPermissionKeys.ForShell(run.ProjectId is not null, run.AllowedDirectories))
                {
                    await permissions.RequireAsync(userId, key, ct).ConfigureAwait(false);
                }

                if (run.ProjectId is not null)
                {
                    var project = await scope.ProjectAccess.AuthorizeAsync(run.ProjectId, ct).ConfigureAwait(false);
                    if (project.NodeId is not null)
                    {
                        throw new InvalidOperationException("This project executes on its node. Server-side skill scripts cannot execute against it in this version.");
                    }
                }

                return scope.ExecutionSettings.ProviderId;
            }, () => networkSettings.AllowSandboxNetworkAccess)
    {
        _executions = executions;
        executions.ConfigurationChanged += ConfigurationChangedAsync;
    }

    /// <summary>Creates node executors from the working directory and shell mode admitted by the server.</summary>
    internal ShellExecutorFactory(RunContextAccessor runContextAccessor, ILogger<ShellExecutorFactory> logger,
        IExecutionEnvironmentCatalog? environments = null, Func<bool>? allowSandboxNetworkAccess = null)
        : this(runContextAccessor, null, logger, environments,
            (_, _) => Task.FromResult(runContextAccessor.Current?.PermissionPreset == ChatPermissionPreset.FullAccess ? runContextAccessor.Current.ConfiguredExecutionProviderId : runContextAccessor.Current?.ConfiguredExecutionProviderId ?? runContextAccessor.Current?.ExecutionProviderId),
            allowSandboxNetworkAccess ?? (() => false))
    {
    }

    internal ShellExecutorFactory(
        RunContextAccessor runContextAccessor,
        IUserDirectoryService? userDirectoryService,
        ILogger<ShellExecutorFactory> logger,
        IExecutionEnvironmentCatalog? environments,
        Func<string, CancellationToken, Task<string?>> resolveModeAsync,
        Func<bool> allowSandboxNetworkAccess)
    {
        _runContextAccessor = runContextAccessor;
        _userDirectoryService = userDirectoryService;
        _environments = environments ?? new ExecutionEnvironmentCatalog(logger);
        _resolveModeAsync = resolveModeAsync;
        _allowSandboxNetworkAccess = allowSandboxNetworkAccess;
    }

    /// <summary>
    /// Gets the executor for the current run, creating it from the user's selected execution environment when needed.
    /// </summary>
    public async Task<ShellExecutor> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        RunContext run = _runContextAccessor.Current
            ?? throw new InvalidOperationException("Shell command execution requires an active run context.");

        string? mode = await GetRunModeAsync(run, cancellationToken).ConfigureAwait(false);
        bool allowNetwork = !run.AirGapped && _allowSandboxNetworkAccess();
        string networkMode = allowNetwork ? "permitted" : "none";
        if (mode is null) { throw new InvalidOperationException("Shell execution is disabled. Select an execution environment in Settings > Agents & AI > Execution."); }
        // Capture admission before resolving the provider so deactivation cannot admit a stale instance.
        var key = (_environments as ExecutionEnvironmentCatalog)?.ConfigurationKey(mode);
        var version = key is null || _executions is null ? 0 : _executions.GetConfigurationVersion(key);
        var definition = _environments.Find(mode)
            ?? throw new InvalidOperationException($"Execution environment '{mode}' is unavailable. Enable its plugin on this device and restart, or save another environment.");
        if (run.AirGapped && !definition.SupportsOffline)
        { throw new InvalidOperationException("Offline mode is on: this execution environment cannot guarantee no-egress. Select a provider that supports offline isolation."); }

        var provider = _environments.GetProvider(mode);
        if (_executions is not null && key is not null)
        { run.ConfigurationScope?.Track(_executions, key, version); }

        string workspace = ResolveWorkspaceDirectoryPath(run);
        while (true)
        {
            if (_byExecutionId.TryGetValue(ExecutorKey(run), out CachedExecutor? existing))
            {
                // Container mounts are fixed at creation, so every reuse must match the current path policy.
                if (existing.Matches(mode, networkMode, run, workspace))
                {
                    existing.Touch();
                    return existing.Executor;
                }

                await EvictAgentAsync(ExecutorKey(run)).ConfigureAwait(false);
            }

            if (!run.AllowedDirectories.Any(directory => directory.IsWorkingDirectory)) { Directory.CreateDirectory(workspace); }

            IExecutionSession session = await provider.CreateSessionAsync(new(
                run.ExecutionId, run.CurrentAgentId ?? string.Empty, workspace, run.AllowedDirectories,
                _userDirectoryService?.GetSkillsDirectoryPath(run.UserId),
                CreateExecutionEnvironment(run.AllowedDirectories, definition.UsesHostPaths),
                ShellCommandPolicy.For(run), run.AirGapped, allowNetwork,
                BoundedOutputBuffer.DefaultCapacityBytes - 1024, TimeSpan.FromSeconds(ShellToolset.MaxTimeoutSeconds)), cancellationToken).ConfigureAwait(false);
            ShellExecutor executor = session.Executor;

            bool ownsExecutor = true;
            try
            {
                var holder = new CachedExecutor(session, workspace, mode, networkMode, run.AllowedDirectories);
                if (!_byExecutionId.TryAdd(ExecutorKey(run), holder))
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    ownsExecutor = false;
                    // A concurrent creator may have used another policy; retry through the normal match path.
                    continue;
                }

                ownsExecutor = false;
                if (DelegatedWorkState.CurrentWorker is { } worker)
                {
                    // A worker's shell ends with the worker, before its slot is released.
                    var workerKey = ExecutorKey(run);
                    worker.OnFinished(() => EvictAgentAsync(workerKey));
                }

                return executor;
            }
            catch
            {
                if (ownsExecutor)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    /// <summary>Creates an initialized one-call host executor for an approved foreground run.</summary>
    public async Task<ShellExecutor> CreateHostExecutorAsync(CancellationToken cancellationToken = default)
    {
        RunContext run = _runContextAccessor.Current
            ?? throw new InvalidOperationException("Host command execution requires an active run context.");

        string? mode = await GetRunModeAsync(run, cancellationToken).ConfigureAwait(false);

        // Provider capability enables this action, while foreground admission still controls authority.
        if (_environments.Find(mode)?.AllowsHostCommand != true)
        {
            throw new InvalidOperationException(
                "This execution environment does not support an explicit Host command.");
        }

        if (run.ExecutionKind == RunExecutionKind.TaskRun)
        {
            throw new InvalidOperationException(
                "Host commands require a foreground user decision and cannot run during an unattended task run.");
        }

        if (run.AirGapped)
        {
            throw new InvalidOperationException(
                "Offline mode is on: host command execution cannot guarantee no-egress.");
        }

        string workspace = ResolveWorkspaceDirectoryPath(run);
        if (!run.AllowedDirectories.Any(directory => directory.IsWorkingDirectory)) { Directory.CreateDirectory(workspace); }

        ShellExecutor executor = BuildLocal(workspace, run);
        try
        {
            await executor.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return executor;
        }
        catch
        {
            await executor.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Ensures the current conversation's executor exists and reports the shared shell target.
    /// </summary>
    public async Task<IExecutionSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        var run = _runContextAccessor.Current ?? throw new InvalidOperationException("Shell execution requires an active run.");
        await GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return _byExecutionId[ExecutorKey(run)].Session;
    }

    /// <inheritdoc />
    public async Task<string> PrepareCommandAsync(string command, string? workdir, CancellationToken cancellationToken = default)
    {
        var run = _runContextAccessor.Current ?? throw new InvalidOperationException("Shell execution requires an active run.");
        // Check the authored command before framing it, so anchored policy rules cannot be bypassed by cd.
        if (!ShellCommandPolicy.For(run).Evaluate(new ShellRequest(command)).Allowed)
        {
            throw new ToolFailureException("Shell command was rejected by the execution policy.");
        }

        if (workdir is null && (!OperatingSystem.IsWindows() || command.All(character => character <= 127))) { return command; }

        string? hostPath = null;
        if (workdir is not null)
        {
            if (string.IsNullOrWhiteSpace(workdir)) { throw new ToolFailureException("workdir must name an existing granted directory."); }

            try { hostPath = PathHelper.ResolveAuthorizedHostPath(workdir, run.AllowedDirectories, WorkspacePathAccess.Read); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
            {
                throw new ToolFailureException("workdir is outside the granted directories or contains an unsafe path.", "Use a listed folder on the execution device.");
            }

            if (!Directory.Exists(hostPath)) { throw new ToolFailureException("workdir must name an existing directory.", "Create the directory with the file tools first."); }
        }

        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var definition = _environments.Find(await GetRunModeAsync(run, cancellationToken).ConfigureAwait(false))
            ?? throw new ToolFailureException("The execution environment is unavailable.", "Select an available execution environment and start a new run.");
        string? path = hostPath is null ? null : definition.UsesHostPaths ? hostPath : PathHelper.ResolveToAgentPath(hostPath, run.AllowedDirectories);
        string shell = Path.GetFileNameWithoutExtension(session.ShellBinary) ?? string.Empty;
        if (shell.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || shell.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            if (path is null) { return command; }

            return "$__oscova_cwd = (Get-Location).Path\ntry {\nSet-Location -ErrorAction Stop -LiteralPath '" +
                path.Replace("'", "''", StringComparison.Ordinal) + "'\n" + command +
                "\n$__oscova_ok = $?\n$__oscova_rc = $LASTEXITCODE\nif ($null -eq $__oscova_rc -or ($__oscova_rc -eq 0 -and -not $__oscova_ok)) { $__oscova_rc = [int](-not $__oscova_ok) }\n" +
                "} finally { Set-Location -ErrorAction Stop -LiteralPath $__oscova_cwd }\n$global:LASTEXITCODE = $__oscova_rc";
        }

        if (shell.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (path is null) { return command; }

            if (path.IndexOfAny(['%', '!', '^', '\r', '\n']) >= 0)
            {
                throw new ToolFailureException("workdir contains characters that cmd.exe cannot safely address.", "Select PowerShell on this device.");
            }

            return "pushd \"" + path + "\" || exit /b 1\n" + command +
                "\nset __oscova_rc=%ERRORLEVEL%\npopd\ncmd /c exit %__oscova_rc%";
        }
        // Restore cwd in the same shell so per-call locations do not discard persistent variables.
        string prepared = path is null ? command : "__oscova_cwd=$PWD\ncd '" + path.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "' && {\n" + command +
            "\n}\n__oscova_rc=$?\ncd \"$__oscova_cwd\" && (exit \"$__oscova_rc\")";
        if (!OperatingSystem.IsWindows() || prepared.All(character => character <= 127)) { return prepared; }

        // Persistent POSIX shells can inherit a legacy Windows stdin encoding.
        var encoded = new StringBuilder();
        foreach (byte value in Encoding.UTF8.GetBytes(prepared))
        {
            encoded.Append("\\0").Append(Convert.ToString(value, 8).PadLeft(3, '0'));
        }

        return "eval \"$(printf '%b' '" + encoded + "')\"";
    }

    /// <summary>
    /// Disposes and evicts the cached executor for a conversation.
    /// </summary>
    public async Task EvictAsync(string executionId)
    {
        if (string.IsNullOrWhiteSpace(executionId))
        {
            return;
        }

        foreach (var key in _byExecutionId.Keys.Where(key => key.ExecutionId == executionId))
        {
            await EvictAgentAsync(key).ConfigureAwait(false);
        }
    }

    private async Task EvictAgentAsync((string ExecutionId, string AgentId, string? Mode, bool FullAccess, string? Worker) key)
    {
        if (_byExecutionId.TryRemove(key, out CachedExecutor? holder))
        {
            await holder.Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes executors idle longer than the supplied window unless the caller keeps them warm.
    /// </summary>
    public async Task ReapIdleAsync(TimeSpan idleWindow, Func<string, bool>? keepWarm = null)
    {
        long cutoffTicks = DateTimeOffset.UtcNow.Subtract(idleWindow).UtcTicks;
        foreach (var entry in _byExecutionId)
        {
            if (keepWarm?.Invoke(entry.Key.ExecutionId) == true)
            {
                entry.Value.Touch();
                continue;
            }

            if (Volatile.Read(ref entry.Value.LastActivityTicks) < cutoffTicks)
            {
                await EvictAgentAsync(entry.Key).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Terminates the current foreground command and restores a clean shell without killing detached background jobs.
    /// </summary>
    public async Task RecoverForegroundCommandAsync()
    {
        RunContext? run = _runContextAccessor.Current;
        if (run is null)
        {
            return;
        }

        if (!_byExecutionId.TryGetValue(ExecutorKey(run), out CachedExecutor? holder))
        {
            return;
        }

        holder.Touch();
        if (!await holder.Session.RecoverForegroundAsync(CancellationToken.None).ConfigureAwait(false))
        { await EvictAgentAsync(ExecutorKey(run)).ConfigureAwait(false); }
    }

    /// <summary>Closes cached shell sessions while leaving the factory available for new admitted runs.</summary>
    public async Task EvictAllAsync()
    {
        foreach (var key in _byExecutionId.Keys)
        {
            await EvictAgentAsync(key).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_executions is not null) { _executions.ConfigurationChanged -= ConfigurationChangedAsync; }

        return new(EvictAllAsync());
    }

    private async Task ConfigurationChangedAsync(string key)
    {
        if (key == ExecutionSettings.ConfigurationKey) { await EvictAllAsync().ConfigureAwait(false); return; }

        if (_environments is not ExecutionEnvironmentCatalog catalog) { return; }

        foreach (var entry in _byExecutionId.ToArray().Where(entry => entry.Value.Mode is { } mode && catalog.ConfigurationKey(mode) == key))
        { await EvictAgentAsync(entry.Key).ConfigureAwait(false); }
    }

    internal ExecutionPermissionRegistry.ConfigurationScope? TrackBackgroundConfiguration(RunContext run, Func<Task> stop)
    {
        if (_executions is null) { return null; }

        var scope = new ExecutionPermissionRegistry.ConfigurationScope(run.UserId, CancellationToken.None, stop);
        try
        {
            scope.Track(_executions, ExecutionSettings.ConfigurationKey, _executions.GetConfigurationVersion(ExecutionSettings.ConfigurationKey));
            if (_environments is ExecutionEnvironmentCatalog catalog && run.ExecutionProviderId is { } id && catalog.ConfigurationKey(id) is { } key)
            { scope.Track(_executions, key, _executions.GetConfigurationVersion(key)); }

            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    private static LocalShellExecutor BuildLocal(string workspace, RunContext run) => new(new LocalShellExecutorOptions
    {
        Mode = ShellMode.Persistent,
        WorkingDirectory = workspace,
        ConfineWorkingDirectory = true,
        CleanEnvironment = true,
        Environment = CreateExecutionEnvironment(run.AllowedDirectories, usesHostPaths: true),
        Policy = ShellCommandPolicy.For(run),
        // Just under the 1 MiB save ceiling so a cut stream plus the shell's marker is saved whole.
        MaxOutputBytes = BoundedOutputBuffer.DefaultCapacityBytes - 1024,
        // Backstop only; the per-call timeout is enforced in ShellToolset. Keep this >= the tool ceiling
        // so framework timeout doesn't preempt smaller per-call values. If a call uses the maximum,
        // either timeout may elapse first depending on scheduling. Also governs file-skill scripts.
        Timeout = TimeSpan.FromSeconds(ShellToolset.MaxTimeoutSeconds)
    });

    internal static IReadOnlyDictionary<string, string?> CreateHostEnvironment()
    {
        string[] names =
            ["PATH", "HOME", "USER", "USERNAME", "USERPROFILE", "SystemRoot", "TEMP", "TMP", "PATHEXT"];
        var environment = new Dictionary<string, string?>();
        foreach (string name in names)
        {
            environment[name] = Environment.GetEnvironmentVariable(name);
        }

        return environment;
    }

    private static IReadOnlyDictionary<string, string?> CreateExecutionEnvironment(IReadOnlyList<AllowedDirectory> directories, bool usesHostPaths)
    {
        var environment = usesHostPaths ? new Dictionary<string, string?>(CreateHostEnvironment()) : new Dictionary<string, string?>();
        foreach (var (name, directory) in PathHelper.GetShellRootVariables(directories))
        {
            environment[name] = usesHostPaths ? directory.Value : PathHelper.ResolveToAgentPath(directory.Value, [directory]);
        }

        return environment;
    }

    private async Task<string?> GetRunModeAsync(
        RunContext run,
        CancellationToken cancellationToken)
    {
        if (_executions is not null)
        {
            var version = _executions.GetConfigurationVersion(ExecutionSettings.ConfigurationKey);
            run.ConfigurationScope?.Track(_executions, ExecutionSettings.ConfigurationKey, version);
        }

        string? current = await _resolveModeAsync(run.UserId, cancellationToken).ConfigureAwait(false);
        if (current != (run.PermissionPreset == ChatPermissionPreset.FullAccess ? run.ConfiguredExecutionProviderId : run.ConfiguredExecutionProviderId ?? run.ExecutionProviderId))
        {
            throw new ToolFailureException(
                "Shell execution settings changed during this run.",
                "Start a new run to use the current execution mode.");
        }

        return run.ExecutionProviderId;
    }

    // Workers of one definition share the run's execution ID, so each worker gets its own persistent shell.
    private static (string, string, string?, bool, string?) ExecutorKey(RunContext run) =>
        (run.ExecutionId, run.CurrentAgentId ?? string.Empty, run.ExecutionProviderId,
            run.PermissionPreset == ChatPermissionPreset.FullAccess, DelegatedWorkState.CurrentWorker?.Handle);

    internal static string ResolveWorkspaceDirectoryPath(
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        string preferredWorkspaceDirectoryPath,
        string fallbackWorkspaceDirectoryPath)
    {
        if (allowedDirectories.Any(directory => directory.IsWorkingDirectory))
        {
            return PathHelper.ResolveWorkingDirectoryHostPath(allowedDirectories)!;
        }

        var writableDirectories = allowedDirectories
            .Where(directory =>
                directory.Kind == DirectorySourceKind.LocalPath &&
                directory.AccessMode == WorkspaceAccessMode.ReadWrite)
            .ToList();

        string normalizedPreferredWorkspace = Path.GetFullPath(preferredWorkspaceDirectoryPath);
        AllowedDirectory? preferredWorkspace = writableDirectories.FirstOrDefault(directory =>
            PathHelper.ArePathsEqual(directory.Value, normalizedPreferredWorkspace));
        if (!string.IsNullOrWhiteSpace(preferredWorkspace?.Value))
        {
            return preferredWorkspace.Value;
        }

        AllowedDirectory? primaryWorkspace = writableDirectories.FirstOrDefault();

        return string.IsNullOrWhiteSpace(primaryWorkspace?.Value)
            ? fallbackWorkspaceDirectoryPath
            : primaryWorkspace.Value;
    }

    /// <summary>
    /// Returns the cached shell workspace, or recomputes it if an idle reaper evicted the holder after executor creation.
    /// </summary>
    internal string GetExecutionTargetWorkspaceDirectory(RunContext run)
    {
        if (_byExecutionId.TryGetValue(ExecutorKey(run), out CachedExecutor? holder))
        {
            holder.Touch();
            return holder.WorkspaceDirectory;
        }

        return ResolveWorkspaceDirectoryPath(run);
    }

    private string ResolveWorkspaceDirectoryPath(RunContext run)
    {
        if (_userDirectoryService is null)
        {
            return PathHelper.ResolveWorkingDirectoryHostPath(run.AllowedDirectories)
                ?? throw new InvalidOperationException("Node shell execution requires an authorized project working directory.");
        }

        return ResolveWorkspaceDirectoryPath(run.AllowedDirectories,
            _userDirectoryService.GetWorkspaceDirectoryPath(run.UserId, UserFileNames.DefaultWorkspaceDirectory),
            _userDirectoryService.GetUserDirectoryPath(run.UserId));
    }

    private sealed class CachedExecutor(
        IExecutionSession session,
        string workspaceDirectory,
        string? mode,
        string networkMode,
        IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        public IExecutionSession Session { get; } = session;
        public ShellExecutor Executor => Session.Executor;

        public string WorkspaceDirectory { get; } = workspaceDirectory;

        public string? Mode { get; } = mode;

        public string NetworkMode { get; } = networkMode;

        public IReadOnlyList<AllowedDirectory> AllowedDirectories { get; } = [.. allowedDirectories];

        public long LastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;

        public bool Matches(string? mode, string networkMode, RunContext run, string workspaceDirectory) =>
            Mode == mode &&
            string.Equals(NetworkMode, networkMode, StringComparison.Ordinal) &&
            PathHelper.ArePathsEqual(WorkspaceDirectory, workspaceDirectory) &&
            AllowedDirectories.SequenceEqual(run.AllowedDirectories);

        public void Touch()
        {
            Volatile.Write(ref LastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
    }
}
