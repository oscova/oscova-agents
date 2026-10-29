using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;
using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Conversations;
using Oscova.Agents.Definitions;
using System.Text;
using Oscova.Agents.Core.Users;

namespace Oscova.Agents.Core.Projects;

/// <summary>Resolves owned project folders without granting access to private application storage.</summary>
public sealed class ProjectAccessService(
    ProjectsSettings settings,
    IUserDirectoryService directories,
    string userId,
    string appDataRoot,
    IUserPermissionService permissions,
    NodeProjectExecutor nodes)
{
    public const int MaxDirectoryEntries = 500;
    public const int MaxTextBytes = (int)UserFileLimits.MaxEditSizeBytes;
    public const int MaxTextLines = 5000;

    /// <summary>Registers an eligible folder after its owning machine validates the path.</summary>
    public async Task<ProjectDefinition> CreateAsync(string name, string rootPath, string? nodeId, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100)
        {
            throw new ArgumentException("Project name must contain 1 to 100 characters.", nameof(name));
        }

        await permissions.RequireAsync(userId, UserPermissionKeys.Projects, ct).ConfigureAwait(false);
        ValidatedProjectRoot root;
        if (nodeId is null)
        {
            await RequireServerProjectAsync(ct).ConfigureAwait(false);
            var path = ValidateRoot(rootPath);
            root = new(path, PathHelper.GetPathComparisonKey(path));
        }
        else
        {
            var candidate = new ProjectDefinition(Guid.NewGuid().ToString("N"), name, rootPath, nodeId, string.Empty);
            var result = await nodes.InvokeAsync(userId, candidate, ProjectCapability.ValidateRoot,
                new { }, [UserPermissionKeys.Projects], null, ct).ConfigureAwait(false);
            root = JsonSerializer.Deserialize<ValidatedProjectRoot>(result, AIJsonUtilities.DefaultOptions)
                ?? throw new InvalidDataException("The node returned no validated project root.");
        }

        await permissions.RequireAsync(userId, UserPermissionKeys.Projects, ct).ConfigureAwait(false);
        return await settings.AddAsync(name, root.Path, nodeId, root.Key, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves owned metadata and revalidates physical roots only on their owning machine.</summary>
    public ProjectDefinition Resolve(string projectId)
    {
        var project = settings.Projects.FirstOrDefault(project => project.Id == projectId)
            ?? throw new KeyNotFoundException("Project is unavailable. Select an existing project or add its folder again.");
        if (project.NodeId is null) { ValidateRoot(project.RootPath); }

        return project;
    }

    /// <summary>Checks current permission before an owned project is used through a server entry point.</summary>
    public async Task<ProjectDefinition> AuthorizeAsync(string projectId, CancellationToken ct = default)
    {
        await permissions.RequireAsync(userId, UserPermissionKeys.Projects, ct).ConfigureAwait(false);
        var project = Resolve(projectId);
        if (project.NodeId is null) { await RequireServerProjectAsync(ct).ConfigureAwait(false); }

        return project;
    }

    /// <summary>Rechecks project permission for each bounded artifact read on its owning node.</summary>
    public async Task<NodeFileChunk> ReadNodeFileAsync(string projectId, string path, long offset, int count, CancellationToken ct)
    {
        var project = await AuthorizeAsync(projectId, ct).ConfigureAwait(false);
        var result = await nodes.InvokeAsync(userId, project, ProjectCapability.ReadFileChunk,
            new { path, offset, count }, [UserPermissionKeys.Projects], null, ct).ConfigureAwait(false);
        return result.Deserialize<NodeFileChunk>(AIJsonUtilities.DefaultOptions)
            ?? throw new InvalidDataException("The node returned no file segment.");
    }

    private async Task RequireServerProjectAsync(CancellationToken ct)
    {
        if (!await permissions.IsOperatorAsync(userId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Server folders can be registered as projects only by the operator. Select your enrolled node.");
        }
    }

    /// <summary>Grants server-local projects without treating a remote device path as a server path.</summary>
    public IReadOnlyList<AllowedDirectory> ResolveRunDirectories(string? projectId, string? mode)
    {
        if (projectId is null) { return []; }

        var project = Resolve(projectId);
        return project.NodeId is null
            ? [new AllowedDirectory(project.RootPath, project.Name, DirectorySourceKind.LocalPath,
                WorkspaceAccessMode.ReadWrite, AgentPath: "/project", IsWorkingDirectory: true)]
            : [];
    }

    /// <summary>Selects file grants and restores the local project alias after physical-root deduplication.</summary>
    public IReadOnlyList<AllowedDirectory> ResolveFileDirectories(
        IReadOnlyList<AllowedDirectory> allowedDirectories, string? projectId, bool workspace)
    {
        var project = projectId is null ? null : Resolve(projectId);
        if (project is not { NodeId: null }) { return allowedDirectories; }

        if (!workspace)
        {
            var projectGrant = allowedDirectories.FirstOrDefault(directory => PathHelper.ArePathsEqual(directory.Value, project.RootPath));
            if (projectGrant is null || allowedDirectories.Any(directory => directory.AgentPath == "/project")) { return allowedDirectories; }
            // The existing grant supplies permission, while the selected project supplies its alias.
            return [.. allowedDirectories, projectGrant with { AgentPath = "/project", IsWorkingDirectory = false }];
        }
        // A project can share a managed workspace root, so retain its independent grant.
        var workspaceDirectories = allowedDirectories.Where(directory => directory.AgentPath != "/project" ||
            !PathHelper.ArePathsEqual(directory.Value, project.RootPath));
        return workspaceDirectories.Select(directory => directory.IsWorkingDirectory &&
            PathHelper.ArePathsEqual(directory.Value, project.RootPath)
                ? directory with { IsWorkingDirectory = false } : directory).ToArray();
    }

    /// <summary>Reconstructs current thread access, including an authorized producing agent's shared ancestors.</summary>
    public async Task<IReadOnlyList<AllowedDirectory>> ResolveThreadDirectoriesAsync(
        ChatThread thread, AgentCompositionService crew, IAgentDefinitionRepository definitions,
        string? producingAgentId = null, CancellationToken ct = default)
    {
        var parent = string.IsNullOrWhiteSpace(thread.AgentId)
            ? await definitions.GetMainAgentAsync(userId, ct).ConfigureAwait(false)
            : await definitions.GetAgentAsync(userId, thread.AgentId, ct).ConfigureAwait(false);
        if (parent is null) { throw new KeyNotFoundException("Conversation agent is unavailable."); }

        var standard = ResolveRunDirectories(thread.ProjectId, null);
        var grants = crew.ResolveAllowedDirectories(userId, parent, standard);
        if (producingAgentId is null || parent.Id == producingAgentId) { return grants; }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        async Task<IReadOnlyList<AllowedDirectory>?> FindAsync(AgentDefinition current, IReadOnlyList<AllowedDirectory> access)
        {
            if (!visited.Add(current.Id)) { return null; }

            foreach (var id in current.AgentToolIds)
            {
                var child = await definitions.GetAgentAsync(userId, id, ct).ConfigureAwait(false);
                if (child?.IsExposedAsTool != true) { continue; }

                var childAccess = crew.ResolveDelegatedDirectories(userId, child, access);
                if (child.Id == producingAgentId) { return childAccess; }

                var found = await FindAsync(child, childAccess).ConfigureAwait(false);
                if (found is not null) { return found; }
            }

            return null;
        }

        var inherited = await FindAsync(parent, grants).ConfigureAwait(false);
        if (inherited is not null) { return inherited; }
        // An earlier agent's artifacts retain its own current grants after the chat switches agents.
        var producer = await definitions.GetAgentAsync(userId, producingAgentId, ct).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The producing agent is unavailable.");
        return crew.ResolveAllowedDirectories(userId, producer);
    }

    /// <summary>Resolves a browser-relative address beneath its selected root before physical file access.</summary>
    public static string ResolveRelativePath(AllowedDirectory root, string? relativePath)
    {
        relativePath ??= string.Empty;
        if (Path.IsPathRooted(relativePath)) { throw new ArgumentException("File path must be relative to the selected folder."); }

        var path = Path.GetFullPath(Path.Combine(root.Value, relativePath));
        if (!PathHelper.IsPathWithinDirectory(path, root.Value))
        {
            throw new UnauthorizedAccessException("File path must stay inside the selected folder.");
        }

        return path;
    }

    /// <summary>Returns opaque root identifiers derived from the current server-resolved grants.</summary>
    public static IReadOnlyList<ProjectFileRoot> GetRoots(IReadOnlyList<AllowedDirectory> directories) =>
        directories.Select(directory => new ProjectFileRoot(GetRootId(directory),
            directory.Label, directory.IsWorkingDirectory, directory.AgentPath == PathHelper.SelfAgentPath,
            directory.AccessMode == WorkspaceAccessMode.ReadOnly)).OrderByDescending(root => root.IsProject).ToArray();

    /// <summary>Lists direct children through the same held-handle boundary used by file tools.</summary>
    public static ProjectFileListing ListFiles(IReadOnlyList<AllowedDirectory> directories, string rootId, string? path)
    {
        var root = SelectRoot(directories, rootId);
        var files = new WorkspaceFileAccess(() => directories);
        var entries = files.Enumerate(ResolveRelativePath(root, path))
            .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            .Take(MaxDirectoryEntries + 1).ToArray();
        var visible = entries.Take(MaxDirectoryEntries).Select(entry => new ProjectFileEntry(
            Path.GetFileName(entry.Path), PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(root.Value, entry.Path)),
            (entry.Attributes & FileAttributes.Directory) != 0, entry.Length,
            entry.Path, PathHelper.ResolveToAgentPath(entry.Path, directories)))
            .OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(visible, entries.Length > MaxDirectoryEntries);
    }

    /// <summary>Opens a selected file under the current grants and returns a bounded text preview.</summary>
    public static async Task<ProjectFilePreview> ReadFileAsync(IReadOnlyList<AllowedDirectory> directories,
        string rootId, string path, CancellationToken ct)
    {
        var files = new WorkspaceFileAccess(() => directories);
        using var stream = files.OpenRead(ResolveRelativePath(SelectRoot(directories, rootId), path));
        return await ReadTextAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves an opaque browser root using only the current authorized directory grants.</summary>
    public static AllowedDirectory SelectRoot(IReadOnlyList<AllowedDirectory> directories, string rootId)
    {
        return directories.FirstOrDefault(directory => GetRootId(directory) == rootId)
            ?? throw new KeyNotFoundException("The selected folder is unavailable. Refresh the folder list.");
    }

    private static string GetRootId(AllowedDirectory directory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.Value)));

    /// <summary>Reads a held stream within the text byte and line limits without returning partial file contents.</summary>
    public static async Task<ProjectFilePreview> ReadTextAsync(Stream stream, CancellationToken ct)
    {
        if (stream.Length > MaxTextBytes) { return new(ProjectReadState.TooLarge); }

        var bytes = new byte[MaxTextBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0) { break; }

            count += read;
        }

        if (count > MaxTextBytes) { return new(ProjectReadState.TooLarge); }

        return StoredFileEditing.Decode(bytes.AsSpan(0, count).ToArray());
    }

    private string ValidateRoot(string path) => ProjectRootValidator.Validate(path, root =>
    {
        var workspace = directories.GetWorkspaceRootPath(userId);
        var library = directories.GetWorkspaceDirectoryPath(userId, UserFileNames.LibraryWorkspaceDirectory);
        if (PathHelper.IsPathWithinDirectory(root, workspace) && !Overlaps(root, library)) { return false; }

        return Overlaps(root, appDataRoot);
    }).Path;

    private static bool Overlaps(string first, string second) =>
        PathHelper.IsPathWithinDirectory(first, second) || PathHelper.IsPathWithinDirectory(second, first);
}
