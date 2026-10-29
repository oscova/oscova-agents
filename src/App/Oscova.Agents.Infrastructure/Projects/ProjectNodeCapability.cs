using System.Reflection;
using System.Runtime.InteropServices;
using Oscova.Agents.Providers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Collections;
using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Execution;
using Oscova.Agents.Skills;
using Oscova.Agents.Middleware;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Projects;

/// <summary>Runs existing file, repository and shell operations inside an admitted local project.</summary>
public sealed class ProjectNodeCapability : INodeCapability
{
    private readonly string _userId;
    private readonly IReadOnlyList<string> _privateRoots;
    private readonly RunContextAccessor _runs = new();
    private readonly NodeToolOutputStore _output = new();
    private readonly ShellExecutorFactory _shell;
    private readonly BackgroundShellJobRegistry _jobs;
    private readonly ShellExecutorReaper _reaper;
    private readonly AgentToolCollection _tools = [];
    private readonly HashSet<string> _shellNames;
    private readonly AsyncLocal<NodeCallbackHandler?> _callback = new();
    private readonly AsyncLocal<bool> _allowNetwork = new();
    private readonly ILoggerFactory _logs;
    private readonly IExecutionEnvironmentCatalog _environments;

    /// <summary>Creates project execution with no central account data or model runtime on the device.</summary>
    public ProjectNodeCapability(string userId, IReadOnlyList<string> privateRoots,
        ExecutionPermissionRegistry executions, ILoggerFactory logs, IExecutionEnvironmentCatalog? environments = null)
    {
        _userId = userId;
        _privateRoots = privateRoots;
        _logs = logs;
        _environments = environments ?? new ExecutionEnvironmentCatalog(logs.CreateLogger<ExecutionEnvironmentCatalog>());
        _shell = new(_runs, logs.CreateLogger<ShellExecutorFactory>(), _environments, () => _allowNetwork.Value);
        _jobs = new(_shell, _runs, logs.CreateLogger<BackgroundShellJobRegistry>(), executions);
        _reaper = new(_shell, _jobs, logs.CreateLogger<ShellExecutorReaper>());
        Func<IReadOnlyList<AllowedDirectory>> directories = () => _runs.Current?.AllowedDirectories ?? [];
        foreach (var toolset in ProjectToolsets.Create(directories, new WorkspaceFileAccess(directories), _shell, _jobs, _output))
        {
            _tools.Add(toolset);
        }

        _shellNames = _tools.Where(tool => tool.UnderlyingMethod?.DeclaringType == typeof(ShellToolset))
            .Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var git = new ProjectGitService(logs.CreateLogger<ProjectGitService>());
        _tools.Add(AIFunctionFactory.Create(() => new ValidatedProjectRoot(CurrentRoot.Value,
            PathHelper.GetPathComparisonKey(CurrentRoot.Value)), name: ProjectCapability.ValidateRoot));
        _tools.Add(AIFunctionFactory.Create((string? path) => ProjectAccessService.ListFiles(
            directories(), ProjectAccessService.GetRoots(directories())[0].Id, path), name: ProjectCapability.ListFiles));
        _tools.Add(AIFunctionFactory.Create((string path, CancellationToken ct) => ProjectAccessService.ReadFileAsync(
            directories(), ProjectAccessService.GetRoots(directories())[0].Id, path, ct), name: ProjectCapability.Preview));
        _tools.Add(AIFunctionFactory.Create((CancellationToken ct) => git.GetChangesAsync(CurrentProject, ct), name: ProjectCapability.Changes));
        _tools.Add(AIFunctionFactory.Create((ProjectChange selection, CancellationToken ct) =>
            git.GetChangeAsync(CurrentProject, selection, directories(), ct), name: ProjectCapability.Change));
        _tools.Add(AIFunctionFactory.Create(GetEnvironmentAsync, name: ProjectCapability.Environment));
        _tools.Add(AIFunctionFactory.Create(ReadFileChunkAsync, name: ProjectCapability.ReadFileChunk));
        _tools.Add(AIFunctionFactory.Create((string path, CancellationToken ct) =>
            StoredFileEditing.ReadAsync(directories(), ResolveEditablePath(path), ct),
            name: ProjectCapability.ReadEditableFile));
        _tools.Add(AIFunctionFactory.Create((string path, byte[] bytes, string expectedRevision, CancellationToken ct) =>
            StoredFileEditing.SaveAsync(directories(), ResolveEditablePath(path),
                bytes, expectedRevision, ct), name: ProjectCapability.SaveEditableFile));
        _tools.Add(AIFunctionFactory.Create(RunSkillAsync, name: ProjectCapability.RunSkill));
        _shellNames.Add(ProjectCapability.RunSkill);
        Description = new(ProjectCapability.Id, ProjectCapability.Version,
            _tools.Select(tool => new NodeTool(tool.Name, tool.Description, tool.JsonSchema)).ToArray());
    }

    private string ResolveEditablePath(string path) => Path.IsPathRooted(path)
        ? PathHelper.ResolveAuthorizedHostPath(path, _runs.Current!.AllowedDirectories, WorkspacePathAccess.Read)
        : ProjectAccessService.ResolveRelativePath(CurrentRoot, path);

    public NodeCapability Description { get; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct) => _reaper.StartAsync(ct);

    /// <inheritdoc />
    public async Task CleanupExecutionAsync(string executionId, CancellationToken ct)
    {
        try { await _jobs.CleanupExecutionAsync(executionId, ct).ConfigureAwait(false); }
        finally { await _shell.EvictAsync(executionId).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetRequiredPermissions(string operation)
    {
        if (_tools.Find(operation) is null) { throw new InvalidOperationException("The project operation is unsupported."); }

        return _shellNames.Contains(operation)
            ? UserPermissionKeys.ForShell(project: true)
            : [UserPermissionKeys.Projects];
    }

    /// <inheritdoc />
    public async Task<NodeReply> InvokeAsync(NodeInvocation invocation, CancellationToken ct, NodeCallbackHandler? callback = null)

    {
        var tool = _tools.Find(invocation.Tool)
            ?? throw new InvalidOperationException("The project operation is unsupported.");
        var context = invocation.Context.Deserialize<ProjectNodeContext>()
            ?? throw new ArgumentException("Project execution context is required.");
        if (_shellNames.Contains(invocation.Tool) && context.ProviderId is not null && _environments.Find(context.ProviderId) is null)
        {
            throw new InvalidOperationException("The selected execution environment is unavailable on this device. Prepare and enable its plugin, or save another environment in Execution settings.");
        }

        if ((context.ConfiguredProviderId is { } configured && string.IsNullOrWhiteSpace(configured)) ||
            (context.FullAccess && context.ProviderId != ConversationExecutionAccess.ResolveProviderId(
                ChatPermissionPreset.FullAccess, context.ConfiguredProviderId ?? null)))
        {
            throw new InvalidOperationException("Full access requires an admitted Host shell with execution enabled.");
        }

        var root = ProjectRootValidator.ValidateNode(context.RootPath, _privateRoots);
        IReadOnlyList<AllowedDirectory> directories =
        [
            new(root.Path, "project", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite,
                AgentPath: "/project", IsWorkingDirectory: true)
        ];
        await using var scope = _runs.SetContext(new RunContext
        {
            UserId = _userId,
            ProjectId = context.ProjectId,
            ExecutionId = invocation.ExecutionId,
            CurrentAgentId = context.AgentId,
            ExecutionKind = RunExecutionKind.WebChat,
            AllowedDirectories = directories,
            StandardRunDirectories = directories,
            ExecutionProviderId = context.ProviderId,
            ConfiguredExecutionProviderId = context.ConfiguredProviderId,
            PermissionPreset = context.FullAccess ? ChatPermissionPreset.FullAccess : ChatPermissionPreset.UseAgentPolicy,
            ExecutionDevice = Environment.MachineName,
            AirGapped = context.AirGapped
        });
        _output.Begin();
        _callback.Value = callback;
        _allowNetwork.Value = context.AllowSandboxNetworkAccess;
        try
        {
            var values = invocation.Arguments.Deserialize<Dictionary<string, object?>>()
                ?? throw new ArgumentException("Tool arguments must be an object.");
            var arguments = new AIFunctionArguments(values);
            WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(tool, arguments, directories);
            ct.ThrowIfCancellationRequested();
            var value = await tool.InvokeAsync(arguments, ct).ConfigureAwait(false);
            string? filePath = null;
            var pathParameter = tool.UnderlyingMethod?.GetParameters()
                .SingleOrDefault(parameter => parameter.GetCustomAttribute<WorkspacePathAttribute>() is not null);
            if (pathParameter?.Name is { } pathName && arguments.TryGetValue(pathName, out var requested) &&
                PathHelper.TryResolveToHostPath(requested?.ToString() ?? string.Empty, directories, root.Path, out var hostPath))
            {
                filePath = PathHelper.ResolveToAgentPath(hostPath, directories);
            }

            var reply = new NodeReply(JsonSerializer.SerializeToElement(value, tool.JsonSerializerOptions), null, _output.Finish(), filePath);
            NodeProtocol.ValidateSize(reply);
            return reply;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Canceling a persistent shell request must also stop its running process.
            if (_shellNames.Contains(invocation.Tool)) { await _shell.EvictAsync(invocation.ExecutionId).ConfigureAwait(false); }

            throw;
        }
        finally { _callback.Value = null; _allowNetwork.Value = false; _output.Finish(); }
    }

    /// <summary>Reads a bounded segment through the same physical file guards as project tools.</summary>
    private async Task<NodeFileChunk> ReadFileChunkAsync(string path, long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count < 0 || count > ProjectCapability.FileChunkBytes)
        {
            throw new ArgumentException($"offset must be nonnegative and count must be between 0 and {ProjectCapability.FileChunkBytes}.");
        }

        var directories = _runs.Current!.AllowedDirectories;
        var hostPath = PathHelper.ResolveAuthorizedHostPath(path, directories, WorkspacePathAccess.Read);
        using var stream = new WorkspaceFileAccess(() => directories).OpenRead(hostPath);
        var length = stream.Length;
        var revision = File.GetLastWriteTimeUtc(hostPath).Ticks;
        if (offset > length) { throw new ArgumentException("offset exceeds the file length."); }

        stream.Position = offset;
        var bytes = new byte[(int)Math.Min(count, length - offset)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return new(length, revision, bytes);
    }

    /// <summary>Runs a bounded copy of the selected skill with the existing project shell executor.</summary>
    private async Task<object?> RunSkillAsync(string skillName, string scriptPath, IReadOnlyList<string> arguments,
        int packageSize, CancellationToken ct)
    {
        var callback = _callback.Value ?? throw new InvalidOperationException("Skill transfer requires an admitted server callback.");
        if (packageSize <= 0 || packageSize > SkillImportLimits.MaxUploadSizeBytes)
        {
            throw new ArgumentException($"packageSize must be between 1 and {SkillImportLimits.MaxUploadSizeBytes} bytes.");
        }

        var staging = Path.Combine(_privateRoots[0], "skill-executions", Guid.NewGuid().ToString("N"));
        try
        {
            using var package = new MemoryStream(packageSize);
            while (package.Length < packageSize)
            {
                var chunk = await callback(ProjectCapability.ReadSkillChunk,
                    JsonSerializer.SerializeToElement(new { offset = checked((int)package.Length) }), ct).ConfigureAwait(false);
                var bytes = Convert.FromBase64String(chunk.GetString() ?? string.Empty);
                if (bytes.Length != Math.Min(ProjectCapability.SkillChunkBytes, packageSize - package.Length))
                {
                    throw new InvalidDataException("The skill package chunk has an unexpected length.");
                }

                await package.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            package.Position = 0;
            var skillsRoot = Path.Combine(staging, "skills");
            var importer = new SkillImportService(_ => skillsRoot, _logs.CreateLogger<SkillImportService>());
            await importer.ImportTrustedPackageAsync(_userId, skillName, package, false, ct).ConfigureAwait(false);
            var root = Path.Combine(skillsRoot, skillName);
            var executable = Path.GetFullPath(Path.Combine(root, scriptPath));
            if (!PathHelper.IsPathWithinDirectory(executable, root) || PathHelper.HasSymlinkInPath(executable, root))
            {
                throw new UnauthorizedAccessException("scriptPath must stay within the transferred skill package.");
            }

            if (!File.Exists(executable)) { throw new FileNotFoundException("The selected skill script is missing.", scriptPath); }

            FileSkillScriptRunner.TryEnsureExecutableOnUnix(executable);
            var run = _runs.Current!;
            // Mount only this transferred bundle, not the node's private application directory.
            await using var skillScope = _runs.SetContext(run.With(allowedDirectories:
                [.. run.AllowedDirectories, new(skillsRoot, "skills", DirectorySourceKind.LocalPath,
                    WorkspaceAccessMode.ReadOnly, AgentPath: "/skills")]));
            var executor = await _shell.GetOrCreateAsync(ct).ConfigureAwait(false);
            var session = await _shell.GetSessionAsync(ct).ConfigureAwait(false);
            var command = FileSkillScriptRunner.BuildCommand(session.ResolveScriptPath(executable), arguments, session.ShellBinary);
            command = await _shell.PrepareCommandAsync(command, null, ct).ConfigureAwait(false);
            try
            {
                var result = await executor.RunAsync(command, ct).ConfigureAwait(false);
                return ShellToolset.FormatResult(result, _output);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await _shell.RecoverForegroundCommandAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync()
    {
        try { await _jobs.CleanupUserAsync(_userId, CancellationToken.None).ConfigureAwait(false); }
        finally { await _shell.EvictAllAsync().ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _reaper.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            _reaper.Dispose();
            try { await _jobs.DisposeAsync().ConfigureAwait(false); }
            finally { await _shell.DisposeAsync().ConfigureAwait(false); }
        }
    }

    /// <summary>Reports this device's shell facts without starting a command or reading server settings.</summary>
    private async Task<RuntimeEnvironmentInfo> GetEnvironmentAsync()
    {
        var definition = _environments.Find(_runs.Current!.ExecutionProviderId)
            ?? (_runs.Current.ExecutionProviderId is null ? HostExecutionProvider.Definition : null);
        // File-only project context remains usable while its shell plugin is unavailable.
        if (definition is null)
        {
            return new RuntimeEnvironmentInfo("unavailable", RuntimeInformation.OSArchitecture.ToString(), "unavailable", "/",
                "not available", "not available", "not available", UsesHostPaths: false);
        }

        await using var shell = definition.Id == HostExecutionProvider.Definition.Id
            ? new LocalShellExecutor(new LocalShellExecutorOptions { Mode = ShellMode.Persistent }) : null;
        return new RuntimeEnvironmentInfo(definition.Os, RuntimeInformation.OSArchitecture.ToString(), shell?.ResolvedShellBinary ?? definition.Shell,
            definition.UsesHostPaths ? Path.DirectorySeparatorChar.ToString() : "/", definition.UsesHostPaths ? CurrentRoot.Value : CurrentRoot.AgentPath!,
            definition.HomeDirectory ?? (definition.UsesHostPaths ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : "not available"),
            definition.TempDirectory ?? (definition.UsesHostPaths ? Path.GetTempPath() : "not available"), definition.UsesHostPaths);
    }

    private AllowedDirectory CurrentRoot => _runs.Current!.AllowedDirectories.Single(directory => directory.IsWorkingDirectory);
    private ProjectDefinition CurrentProject => new(_runs.Current!.ProjectId!, "project", CurrentRoot.Value,
        null, PathHelper.GetPathComparisonKey(CurrentRoot.Value));
}
