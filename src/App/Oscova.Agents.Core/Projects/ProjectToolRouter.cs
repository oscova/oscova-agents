using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Harness;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Middleware;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Plugins;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Core.Projects;

/// <summary>Routes granted file aliases to their owner and keeps relative paths on the selected execution device.</summary>
public sealed class ProjectToolRouter(RunContextAccessor runs, ProjectAccessService projects,
    IUserPermissionService permissions, ExecutionPermissionRegistry executions, NodeProjectExecutor nodes,
    ExecutionSettings shellSettings, IAgentPluginCatalog plugins, INodePluginExecutor pluginNodes)
{
    /// <summary>Identifies the resolved file owner retained with a tool call for artifact access.</summary>
    public const string FileEnvironmentMetadataKey = "oscova.file.environment";

    /// <summary>Identifies the granted canonical address retained for equivalent file references.</summary>
    public const string FilePathMetadataKey = "oscova.file.path";

    /// <summary>Adds routing only to the existing file and shell toolsets shared with device projects.</summary>
    public AIFunction Decorate(AIFunction tool)
    {
        var owner = tool.UnderlyingMethod?.DeclaringType;
        var file = owner == typeof(CodingToolset) || owner == typeof(DirectoryToolset);
        var shell = IsShellToolset(owner);
        var pluginId = plugins.Plugins.FirstOrDefault(plugin => plugin.Toolsets.Any(contribution =>
            contribution.SupportsNodeExecution && contribution.ToolsetType == owner))?.Manifest.Id;
        if (file || shell || pluginId is not null)
        {
            return new RoutedFunction(tool, this, file, pluginId);
        }

        return GetWorkspacePathParameters(tool).Any() ? new SharedFolderFunction(tool, this) : tool;
    }

    /// <summary>Reports whether a toolset type runs shell commands and therefore needs a shell grant.</summary>
    public static bool IsShellToolset(Type? type) => type == typeof(ShellToolset) || type == typeof(HostShellToolset);

    /// <summary>Reports whether the account holds every shell grant for a workspace or project run.</summary>
    public async Task<bool> IsShellGrantedAsync(string userId, bool project, CancellationToken ct)
    {
        var granted = await permissions.GetGrantedAsync(userId, ct).ConfigureAwait(false);
        return UserPermissionKeys.ForShell(project).All(granted.Contains);
    }

    private async ValueTask<object?> InvokeAsync(AIFunction tool, bool file, string? pluginId, AIFunctionArguments arguments, CancellationToken ct)
    {
        var run = runs.Current ?? throw new InvalidOperationException("Tool execution requires an active run.");
        if (pluginId is not null)
        {
            if (run.ProjectId is { } projectId)
            {
                var target = await projects.AuthorizeAsync(projectId, ct).ConfigureAwait(false);
                if (target.NodeId is not null)
                {
                    var result = await pluginNodes.InvokeAsync(pluginId, run, target, tool, arguments, ct).ConfigureAwait(false);
                    return result.ValueKind == JsonValueKind.String ? result.GetString() : result;
                }
            }

            WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(tool, arguments, run.AllowedDirectories);
            return await InvokeHoldingSharedFolderAsync(tool, arguments, ct).ConfigureAwait(false);
        }

        var values = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        var environment = run.ProjectId is null ? "workspace" : "project";
        object? selected = null;
        var explicitEnvironment = file && values.Remove("environment", out selected);
        if (explicitEnvironment)
        {
            environment = selected is JsonElement element ? element.GetString() : selected as string;
        }

        if (environment is not ("workspace" or "project"))
        {
            throw new ToolFailureException("environment must be workspace or project.");
        }

        var pathParameter = file ? tool.UnderlyingMethod?.GetParameters()
            .SingleOrDefault(parameter => parameter.GetCustomAttribute<WorkspacePathAttribute>() is not null) : null;
        if (file)
        {
            if (pathParameter?.Name is { } name && values.TryGetValue(name, out var pathValue))
            {
                var path = pathValue is JsonElement element ? element.GetString() : pathValue as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    IReadOnlyList<AllowedDirectory> roots = run.AllowedDirectories;
                    if (run.ProjectId is { } selectedProject)
                    {
                        var target = projects.Resolve(selectedProject);
                        roots = [.. roots, new(target.RootPath, target.Name, DirectorySourceKind.LocalPath,
                            WorkspaceAccessMode.ReadWrite, AgentPath: "/project")];
                    }

                    var owners = PathHelper.GetAgentPathRoots(path, roots)
                        .Select(root => root.AgentPath == "/project" ? "project" : "workspace")
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (owners.Length > 1) { throw new ToolFailureException("The granted path has ambiguous device ownership."); }

                    if (owners.Length == 1)
                    {
                        if (explicitEnvironment && environment != owners[0])
                        {
                            throw new ToolFailureException($"environment conflicts with the path owner. Use {owners[0]} or omit environment.");
                        }

                        environment = owners[0];
                    }
                }
            }
        }

        if (file && FunctionInvokingChatClient.CurrentContext?.CallContent is { } call && call.Name == tool.Name)
        {
            // Retain routing separately from the authored arguments for previews and downloads.
            call.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            call.AdditionalProperties[FileEnvironmentMetadataKey] = environment;
        }

        var project = environment == "project"
            ? projects.Resolve(run.ProjectId ?? throw new ToolFailureException("Select a project before using environment project."))
            : null;
        IReadOnlyList<string> required = file
            ? project is null ? [] : [UserPermissionKeys.Projects]
            : UserPermissionKeys.ForShell(project is not null, run.AllowedDirectories);
        if (file && TargetsSharedFolder(tool, values, run.AllowedDirectories))
        {
            required = [.. required, UserPermissionKeys.SharedFolder];
        }

        if (!file)
        {
            run.ConfigurationScope?.Track(executions, ExecutionSettings.ConfigurationKey,
                executions.GetConfigurationVersion(ExecutionSettings.ConfigurationKey));
        }

        if (!file && (shellSettings.ProviderId != (run.ConfiguredExecutionProviderId ?? run.ExecutionProviderId) ||
            shellSettings.ProviderId == null))
        {
            throw new ToolFailureException("Shell execution is disabled or its mode changed. Start a new run after selecting the intended mode.");
        }

        if (!file)
        {
            await ConversationExecutionAccess.RequireAsync(permissions, run.UserId, run.PermissionPreset, ct).ConfigureAwait(false);
        }

        if (project?.NodeId is not null)
        {
            if (!file) { pluginNodes.RequireExecutionReady(run.UserId, project.NodeId, run.ExecutionProviderId); }

            var result = await nodes.InvokeAsync(run.UserId, project, tool.Name, values, required, run, ct)
                .ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.String ? result.GetString() : result;
        }

        using var execution = executions.Register(run.UserId, required, ct);
        foreach (var key in required)
        {
            await permissions.RequireAsync(run.UserId, key, execution.Token).ConfigureAwait(false);
        }

        if (project is not null) { await projects.AuthorizeAsync(project.Id, execution.Token).ConfigureAwait(false); }

        execution.Token.ThrowIfCancellationRequested();
        var forwarded = new AIFunctionArguments(values) { Services = arguments.Services, Context = arguments.Context };

        var directories = file
            ? projects.ResolveFileDirectories(run.AllowedDirectories, run.ProjectId, environment == "workspace")
            : run.AllowedDirectories;
        await using var scope = runs.SetContext(run.With(allowedDirectories: directories));
        WorkspacePathValidationMiddleware.ValidateWorkspaceArguments(tool, forwarded, directories);
        if (file && pathParameter?.Name is { } pathName && forwarded.TryGetValue(pathName, out var requested) &&
            FunctionInvokingChatClient.CurrentContext?.CallContent is { } fileCall && fileCall.Name == tool.Name &&
            PathHelper.TryResolveToHostPath(requested?.ToString() ?? string.Empty, directories,
                PathHelper.ResolveWorkingDirectoryHostPath(directories), out var hostPath))
        {
            fileCall.AdditionalProperties![FilePathMetadataKey] = PathHelper.ResolveToAgentPath(hostPath, directories);
        }

        return await tool.InvokeAsync(forwarded, execution.Token).ConfigureAwait(false);
    }

    // Calls that reach the shared folder hold its permission, so revoking it cancels them like other grants.
    private async ValueTask<object?> InvokeHoldingSharedFolderAsync(AIFunction tool, AIFunctionArguments arguments, CancellationToken ct)
    {
        var run = runs.Current ?? throw new InvalidOperationException("Tool execution requires an active run.");
        if (!TargetsSharedFolder(tool, arguments, run.AllowedDirectories))
        {
            return await tool.InvokeAsync(arguments, ct).ConfigureAwait(false);
        }

        using var execution = executions.Register(run.UserId, [UserPermissionKeys.SharedFolder], ct);
        try
        {
            await permissions.RequireAsync(run.UserId, UserPermissionKeys.SharedFolder, execution.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // A revocation that lands during the check refuses the call like a missing permission.
            throw new ToolFailureException("The shared folder is not available to this account.", "Ask the operator to turn on Shared folder for this user.");
        }

        try
        {
            return await tool.InvokeAsync(arguments, execution.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Revoking the permission cancels this call alone, so the run continues with a tool failure.
            throw new ToolFailureException("The shared folder stopped being available. Partial file changes may have occurred.", "Ask the operator to turn on Shared folder for this user.");
        }
    }

    private static bool TargetsSharedFolder(AIFunction tool, IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<AllowedDirectory> directories)
    {
        var shared = directories.FirstOrDefault(directory => directory.AgentPath == PathHelper.SharedAgentPath);
        if (shared is null)
        {
            return false;
        }

        var workingDirectory = PathHelper.ResolveWorkingDirectoryHostPath(directories);
        return GetWorkspacePathParameters(tool)
            .SelectMany(name => WorkspacePathValidationMiddleware.GetPathArguments(name, values))
            .Any(path => PathHelper.TryResolveToHostPath(path, directories, workingDirectory, out var hostPath) &&
                PathHelper.IsPathWithinDirectory(hostPath, shared.Value));
    }

    private static IEnumerable<string> GetWorkspacePathParameters(AIFunction tool) =>
        tool.UnderlyingMethod?.GetParameters()
            .Where(parameter => parameter.Name is not null && parameter.GetCustomAttribute<WorkspacePathAttribute>() is not null)
            .Select(parameter => parameter.Name!) ?? [];

    /// <summary>Holds the shared folder permission for other path tools while they work in the shared folder.</summary>
    private sealed class SharedFolderFunction(AIFunction inner, ProjectToolRouter router) : DelegatingAIFunction(inner)
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            router.InvokeHoldingSharedFolderAsync(InnerFunction, arguments, cancellationToken);
    }

    /// <summary>Exposes one routed function with an optional file-environment selector.</summary>
    private sealed class RoutedFunction : DelegatingAIFunction
    {
        private readonly ProjectToolRouter _router;
        private readonly bool _file;
        private readonly string? _pluginId;

        public RoutedFunction(AIFunction inner, ProjectToolRouter router, bool file, string? pluginId) : base(inner)
        {
            _router = router;
            _file = file;
            _pluginId = pluginId;
            var schema = JsonNode.Parse(inner.JsonSchema.GetRawText())!.AsObject();
            if (file)
            {
                if (schema["properties"] is null) { schema["properties"] = new JsonObject(); }

                var properties = schema["properties"]!.AsObject();
                properties["environment"] = JsonSerializer.SerializeToNode(new
                {
                    type = "string",
                    @enum = new[] { "workspace", "project" },
                    description = "Granted aliases select their file owner automatically.\nFor relative or native paths, omit to use the selected project, or workspace without a project.\nUse workspace or project to select a different base for relative or native paths.\nAn explicit environment must agree with an alias owner."
                });
            }

            JsonSchema = JsonSerializer.SerializeToElement(schema);
        }

        public override JsonElement JsonSchema { get; }
        // Paths are validated by the selected executor, never against another machine's filesystem.
        public override MethodInfo? UnderlyingMethod => null;
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            try { return await _router.InvokeAsync(InnerFunction, _file, _pluginId, arguments, cancellationToken).ConfigureAwait(false); }
            catch (UnauthorizedAccessException)
            {
                throw new ToolFailureException("This operation is not permitted in the selected environment.", "Ask the operator to check Projects, Shared folder and the applicable shell permission.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ToolFailureException("The operation was interrupted. Partial file changes may have occurred.", "Check permissions, node connectivity and existing files before retrying.");
            }
        }
    }
}
