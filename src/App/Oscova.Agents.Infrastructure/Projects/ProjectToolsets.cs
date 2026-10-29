using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Projects;

/// <summary>Constructs the file and shell toolsets shared by server workspaces and device projects.</summary>
public static class ProjectToolsets
{
    /// <summary>Creates the existing tools against the supplied execution environment.</summary>
    public static IReadOnlyList<IAgentToolset> Create(
        Func<IReadOnlyList<AllowedDirectory>> directories,
        IWorkspaceFileAccess files,
        IShellExecutorProvider shell,
        IShellBackgroundJobProvider jobs,
        IToolOutputStore output) =>
    [
        new CodingToolset(directories, files, output),
        new DirectoryToolset(directories, files),
        new ShellToolset(shell, jobs, output)
    ];
}
