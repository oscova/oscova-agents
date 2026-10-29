using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Core.Auth;

/// <summary>Names the operator grants accepted by account settings and execution boundaries.</summary>
public static class UserPermissionKeys
{
    public const string Projects = "projects";
    public const string WorkspaceShell = "workspace.shell";
    public const string ProjectShell = "project.shell";
    public const string InstallApprovedSkills = "skills.install-approved";
    public const string ManageCustomSkills = "skills.manage-custom";
    public const string ManagePersonalMcp = "mcp.manage-personal";
    public const string ManagePersonalModelProviders = "models.manage-personal";
    public const string SharedFolder = "shared-folder";

    /// <summary>Gets the supported claims, which are all granted to the operator.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Projects, WorkspaceShell, ProjectShell, InstallApprovedSkills, ManageCustomSkills, ManagePersonalMcp, ManagePersonalModelProviders, SharedFolder];

    /// <summary>Gets the grants required by the selected shell execution surface.</summary>
    public static IReadOnlyList<string> ForShell(bool project) => project
        ? [Projects, ProjectShell]
        : [WorkspaceShell];

    /// <summary>Gets the shell grants plus the shared folder grant when the run holds the shared root.</summary>
    public static IReadOnlyList<string> ForShell(bool project, IEnumerable<AllowedDirectory> directories) =>
        directories.Any(directory => directory.AgentPath == PathHelper.SharedAgentPath)
            ? [.. ForShell(project), SharedFolder]
            : ForShell(project);
}
