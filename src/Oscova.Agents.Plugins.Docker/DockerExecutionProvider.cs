using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Execution;
using Oscova.Agents.Plugins.Docker.Localization;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Plugins.Docker;

/// <summary>Shares the local image coordinator between Docker sessions, MCP launches and image settings.</summary>
public sealed class DockerExecutionProvider : IExecutionEnvironmentProvider, IContainerProcessLauncher
{
    public const string Id = "docker";
    public static ExecutionEnvironmentDefinition Definition { get; } = new(Id, "Docker", "Commands run inside Docker with your granted folders mounted. Host-installed tools are not automatically available.",
        false, true, true, true, "Linux", "bash", "Execution.Title", "Execution.Description", "/root", "/tmp");
    public DockerImageCoordinator Images { get; }
    private readonly SandboxImageBuilder _builder;
    private readonly ILogger _logger;
    /// <summary>Creates the shared image owners from read-only installation settings and device storage.</summary>
    public DockerExecutionProvider(AgentPluginContext context)
    {
        _logger = context.LoggerFactory.CreateLogger<DockerExecutionProvider>();
        var settings = new DockerImageSettings(context.InstallationSettings
            ?? throw new InvalidOperationException("Docker requires installation-scoped plugin settings."));
        _builder = new(settings, context.LoggerFactory.CreateLogger<SandboxImageBuilder>(), context.TimeProvider,
            context.PrivateDataDirectory ?? throw new InvalidOperationException("Docker requires a private device storage root."));
        Images = new(_builder, settings, _logger);
    }
    public bool SupportsNodeExecution => true;
    /// <inheritdoc />
    public async Task<ExecutionAvailabilityResult> CheckAvailabilityAsync(ExecutionAvailabilityRequest request, CancellationToken ct)
    {
        try
        {
            await _builder.EnsureDockerReadyAsync(ct).ConfigureAwait(false);
            return new(true);
        }
        catch (DockerReadinessException ex)
        {
            return new(false, ex.Message, PluginTextKeys.AvailabilityUnavailable);
        }
    }

    /// <inheritdoc />
    public Task StopAsync() => Task.WhenAll(Images.StopAsync(), _builder.StopAsync());
    /// <inheritdoc />
    public async Task<IExecutionSession> CreateSessionAsync(ExecutionSessionRequest request, CancellationToken ct)
    {
        await _builder.EnsureDockerReadyAsync(ct).ConfigureAwait(false);
        if (request.Offline) { await _builder.EnsureOfflineImageAsync(ct).ConfigureAwait(false); }
        else { await _builder.EnsureImageAsync(ct).ConfigureAwait(false); }
        var args = BuildSessionArguments(request.SkillsDirectory, request.Directories, _logger);
        var primary = PathHelper.ResolveWorkingDirectoryAgentPath(request.Directories);
        var executor = new DockerShellExecutor(new DockerShellExecutorOptions
        {
            Image = SandboxImageBuilder.ImageTag, Mode = ShellMode.Persistent, HostWorkdir = request.WorkingDirectory,
            MountReadonly = false, ContainerWorkdir = primary ?? "not available",
            Network = request.AllowNetwork && !request.Offline ? DockerNetworkMode.Bridge : DockerNetworkMode.None,
            ExtraRunArgs = args, Environment = request.Environment.Where(entry => entry.Value is not null)
                .ToDictionary(entry => entry.Key, entry => entry.Value!), Policy = request.Policy, MaxOutputBytes = request.MaxOutputBytes, Timeout = request.Timeout
        });
        try { await executor.InitializeAsync(ct).ConfigureAwait(false); return new Session(executor, request, _logger); }
        catch { await executor.DisposeAsync().ConfigureAwait(false); throw; }
    }
    /// <inheritdoc />
    public async Task<ContainerProcessLaunch> PrepareAsync(ContainerProcessRequest request, CancellationToken ct)
    {
        await _builder.EnsureDockerReadyAsync(ct).ConfigureAwait(false);
        if (request.Offline) { await _builder.EnsureOfflineImageAsync(ct).ConfigureAwait(false); }
        else { await _builder.EnsureImageAsync(ct).ConfigureAwait(false); }
        var args = BuildContainerArguments(request, _logger);
        return new("docker", args, request.Environment);
    }
    private static IReadOnlyList<string> BuildSessionArguments(string? skillsDirectory, IReadOnlyList<AllowedDirectory> directories,
        ILogger? logger)
    {
        var args = new List<string>();
        if (Directory.Exists(skillsDirectory)) { args.Add("-v"); args.Add($"{skillsDirectory}:/skills:ro"); }
        var primary = PathHelper.ResolveWorkingDirectoryAgentPath(directories);
        SandboxMountArgs.AppendDirectoryMounts(args, directories, logger, primary);
        return args;
    }
    private static IReadOnlyList<string> BuildContainerArguments(ContainerProcessRequest request, ILogger logger)
    {
        var args = new List<string> { "run", "-i", "--rm", "--init", "--label", "oscova.mcp-server=" + request.Name };
        if (request.Offline) { args.Add("--network"); args.Add("none"); }
        SandboxMountArgs.AppendDirectoryMounts(args, request.Directories, logger);
        foreach (var key in request.Environment.Keys) { args.Add("-e"); args.Add(key); }
        var directories = request.Directories.Where(directory => Directory.Exists(directory.Value)).ToArray();
        var working = directories.FirstOrDefault(directory => directory.IsWorkingDirectory)
            ?? directories.FirstOrDefault(directory => PathHelper.ResolveToAgentPath(directory.Value, [directory]) == "/workspace/default")
            ?? directories.FirstOrDefault();
        if (working is not null) { args.Add("-w"); args.Add(PathHelper.ResolveToAgentPath(working.Value, [working])); }
        args.Add(SandboxImageBuilder.ImageTag); args.Add(request.Command); args.AddRange(request.Arguments);
        return args;
    }
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    { await Images.DisposeAsync().ConfigureAwait(false); await _builder.DisposeAsync().ConfigureAwait(false); }
    private sealed class Session(DockerShellExecutor executor, ExecutionSessionRequest request, ILogger logger) : IExecutionSession
    {
        public ShellExecutor Executor => executor;
        public string? ShellBinary => "bash";
        public string ResolveScriptPath(string hostPath)
        {
            var full = Path.GetFullPath(hostPath);
            if (request.SkillsDirectory is { } skills && PathHelper.IsPathWithinDirectory(full, skills))
            { return "/skills/" + PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(skills, full)); }
            if (!PathHelper.TryResolveToHostPath(full, request.Directories, request.WorkingDirectory, out _))
            { throw new InvalidOperationException("The script is not mounted into this execution environment."); }
            return PathHelper.ResolveToAgentPath(full, request.Directories);
        }
        public async Task<bool> RecoverForegroundAsync(CancellationToken ct)
        { await KillDockerForegroundShellProcessTreeAsync(executor.DockerBinary, executor.ContainerName).ConfigureAwait(false); return true; }
        public Task<IExecutionBackgroundJob> StartBackgroundAsync(string command, BoundedOutputBuffer buffer, CancellationToken ct)
            => new DockerBackgroundJobs(logger).StartAsync(command, executor, request.Environment, buffer, ct);
        public ValueTask DisposeAsync() => executor.DisposeAsync();
    }
    private static async Task KillDockerForegroundShellProcessTreeAsync(string dockerBinary, string containerName)
    {
        // Kill only MAF's foreground shell tree. In some Docker runtimes the shell PGID can be
        // shared broadly enough that a negative-PGID kill also stops detached background jobs.
        const string Script =
            "kill_tree() { for CHILD in $(pgrep -P \"$1\" 2>/dev/null); do kill_tree \"$CHILD\"; done; kill -KILL \"$1\" 2>/dev/null || true; }; " +
            "PID=$(pgrep -fx 'bash --noprofile --norc' | head -n1); " +
            "[ -n \"$PID\" ] && kill_tree \"$PID\" || true";

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
        startInfo.ArgumentList.Add(Script);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or Win32Exception or InvalidOperationException)
        {
            // Best-effort recovery; the next foreground command will surface any remaining shell issue.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }
    }

}
