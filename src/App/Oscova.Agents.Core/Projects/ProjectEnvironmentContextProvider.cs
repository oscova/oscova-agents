using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Providers;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Core.Projects;

/// <summary>Publishes the selected project's runtime without presenting server folders as node-local folders.</summary>
public sealed class ProjectEnvironmentContextProvider(EnvironmentContextProvider server,
    RunContextAccessor runs, ProjectAccessService projects, NodeProjectExecutor nodes,
    Func<IReadOnlyList<AllowedDirectory>> workspaceDirectories) : AIContextProvider
{
    /// <inheritdoc />
    protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken ct = default)
    {
        var run = runs.Current;
        if (run?.ProjectId is null) { return await server.InvokingAsync(context, ct).ConfigureAwait(false); }

        var project = await projects.AuthorizeAsync(run.ProjectId, ct).ConfigureAwait(false);
        if (project.NodeId is null) { return await server.InvokingAsync(context, ct).ConfigureAwait(false); }

        var result = await nodes.InvokeAsync(run.UserId, project, ProjectCapability.Environment, new { },
            [UserPermissionKeys.Projects], run, ct).ConfigureAwait(false);
        var environment = result.Deserialize<RuntimeEnvironmentInfo>(AIJsonUtilities.DefaultOptions)
            ?? throw new InvalidDataException("The project node returned no environment information.");
        // These grants describe addresses to the model. Only executor-owned grants authorize actual IO.
        IReadOnlyList<AllowedDirectory> descriptions =
        [
            new(project.RootPath, "Selected node project", DirectorySourceKind.LocalPath,
                WorkspaceAccessMode.ReadWrite, AgentPath: "/project", IsWorkingDirectory: true),
            .. workspaceDirectories().Select(directory => directory with
            {
                Label = "Server workspace: " + directory.Label,
                IsWorkingDirectory = false
            })
        ];
        var provider = new EnvironmentContextProvider(descriptions)
        {
            RuntimeEnvironment = environment,
            ShellDirectories = [descriptions[0]],
            HostEnvironment = server.HostEnvironment,
            NotificationChannels = server.NotificationChannels,
            SystemPrompt = server.SystemPrompt + """


                ## Device routing
                Runtime Context describes the selected project node.
                File tools route granted server aliases to the server and /project to the selected project node.
                Relative paths and omitted search paths use the selected project.
                Native paths use the selected project unless environment workspace is specified.
                An explicit environment must agree with the path's alias owner.
                Server workspace paths belong to the server, not to the node shell.
                Shell tools execute only on the selected project node. They cannot access server workspace paths.
                Compatible plugin tools execute on the selected project node. Other plugin tools keep their server execution location.
                MCP tools use their configured execution device. Encrypted-file tools execute on the server.
                Use the skill script tool to execute a server-stored skill against this project. It transfers the script and support files.
                In container mode, project shell paths start at /project. Transferred skill files are mounted read-only under /skills.
                A disconnected project never switches to server execution.
                """
        };
        return await provider.InvokingAsync(context, ct).ConfigureAwait(false);
    }
}
