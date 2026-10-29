using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Projects;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Workspace;
using Oscova.Agents.Host.Server.Services;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Serves owned project registration and inspection on each project's selected execution device.</summary>
[ApiController]
[Authorize]
[Route("api/projects")]
[IgnoreAntiforgeryToken]
public sealed class ProjectsController(UserServiceManager users, ProjectGitService git,
    NodeProjectExecutor nodes,
    WorkspaceFileEditingService editing,
    ArtifactFileAccessService files,
    ThreadRunGuard runs, ILogger<ProjectsController> logger) : UserScopeControllerBase(users)
{
    private const string NodeProjectRoot = "project";

    /// <summary>Lists saved project metadata, including references to disconnected devices.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectDefinition>>> GetProjects(CancellationToken ct) =>
        Ok((await GetUserScopeAsync(ct)).ProjectsSettings.Projects);

    /// <summary>Registers a folder validated by its owning server or node.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CreateProjectRequest request, CancellationToken ct)
    {
        try
        {
            var scope = await GetUserScopeAsync(ct);
            return Ok(await scope.ProjectAccess.CreateAsync(request.Name, request.RootPath, request.NodeId, ct));
        }
        catch (Exception ex) when (TryProblem(ex, out var response)) { return response; }
    }

    /// <summary>Removes a saved reference and cancels its chats without deleting project files.</summary>
    [HttpDelete("{projectId}")]
    public async Task<IActionResult> Remove(string projectId, CancellationToken ct)
    {
        try
        {
            var scope = await GetUserScopeAsync(ct);
            if (!await scope.ProjectsSettings.RemoveAsync(projectId, ct)) { return NotFound(); }

            var ids = await scope.ChatThreadRepository.GetProjectThreadIdsAsync(scope.UserId, projectId, CancellationToken.None);
            foreach (var id in ids) { runs.Cancel(scope.UserId, id); }

            return NoContent();
        }
        catch (Exception ex) when (TryProblem(ex, out var response)) { return response; }
    }

    /// <summary>Lists server workspace roots and the separately addressed node project.</summary>
    [HttpGet("{projectId}/threads/{threadId}/roots")]
    public async Task<IActionResult> Roots(string projectId, string threadId, CancellationToken ct)
    {
        try
        {
            var (project, directories) = await ResolveAsync(projectId, threadId, ct);
            var roots = ProjectAccessService.GetRoots(directories);
            return Ok(project.NodeId is null ? roots :
                new[] { new ProjectFileRoot(NodeProjectRoot, project.Name, true, false, false) }.Concat(roots).ToArray());
        }
        catch (Exception ex) when (TryProblem(ex, out var response)) { return response; }
    }

    /// <summary>Lists bounded direct children on the selected root's owning machine.</summary>
    [HttpGet("{projectId}/files")]
    [HttpGet("{projectId}/threads/{threadId}/files")]
    public async Task<IActionResult> Files(string projectId, string? threadId, string? rootId, string? path, CancellationToken ct)
    {
        try
        {
            var (project, directories) = await ResolveAsync(projectId, threadId, ct);
            ApplyPrivateFileHeaders();
            if (project.NodeId is not null && (rootId is null || rootId == NodeProjectRoot))
            {
                return Ok(await ReadNodeAsync<ProjectFileListing>(project, ProjectCapability.ListFiles, new { path }, ct));
            }

            return Ok(ProjectAccessService.ListFiles(directories, rootId ?? ProjectAccessService.GetRoots(directories)[0].Id, path));
        }
        catch (Exception ex) when (TryProblem(ex, out var response)) { return response; }
    }

    /// <summary>Reads a bounded text preview from the selected root's owning machine.</summary>
    [HttpGet("{projectId}/file")]
    [HttpGet("{projectId}/threads/{threadId}/file")]
    public async Task<IActionResult> FilePreview(string projectId, string? threadId, string? rootId, string path, CancellationToken ct)
    {
        try
        {
            var (project, directories) = await ResolveAsync(projectId, threadId, ct);
            ApplyPrivateFileHeaders();
            if (project.NodeId is not null && (rootId is null || rootId == NodeProjectRoot))
            {
                ValidateNodeFilePath(project, path);
                return Ok(await editing.ReadAsync(GetUserId(), new(path, [], project), ct));
            }

            var root = ProjectAccessService.SelectRoot(directories, rootId ?? ProjectAccessService.GetRoots(directories)[0].Id);
            return Ok(await editing.ReadAsync(GetUserId(), new(ProjectAccessService.ResolveRelativePath(root, path), directories), ct));
        }
        catch (Exception ex) when (TryProblem(ex, out var response)) { return response; }
    }

    /// <summary>Downloads the selected original from its owning device under current project grants.</summary>
    [HttpGet("{projectId}/download")]
    [HttpGet("{projectId}/threads/{threadId}/download")]
    public async Task<IActionResult> Download(string projectId, string? threadId, string? rootId, string path, CancellationToken ct)
    {
        try
        {
            var (project, directories) = await ResolveAsync(projectId, threadId, ct);
            ApplyPrivateFileHeaders();
            WorkspaceFileTarget target;
            if (project.NodeId is not null && (rootId is null || rootId == NodeProjectRoot))
            {
                ValidateNodeFilePath(project, path);
                target = new(path, [], project);
            }
            else
            {
                var root = ProjectAccessService.SelectRoot(directories, rootId ?? ProjectAccessService.GetRoots(directories)[0].Id);
                target = new(ProjectAccessService.ResolveRelativePath(root, path), directories);
            }

            return File(await files.OpenReadAsync(GetUserId(), target, ct), "application/octet-stream",
                path.Split('/').Last(), enableRangeProcessing: true);
        }
        catch (Exception ex) when (TryProblem(ex, out var response)) { return response; }
    }

    /// <summary>Replaces existing owned text on its original device after an expected-revision check.</summary>
    [HttpPut("{projectId}/file")]
    [HttpPut("{projectId}/threads/{threadId}/file")]
    public async Task<IActionResult> SaveFile(string projectId, string? threadId, string? rootId, string path,
        SaveProjectFileRequest request, CancellationToken ct)
    {
        try
        {
            var (project, directories) = await ResolveAsync(projectId, threadId, ct);
            ApplyPrivateFileHeaders();
            WorkspaceFileTarget target;
            if (project.NodeId is not null && (rootId is null || rootId == NodeProjectRoot))
            {
                ValidateNodeFilePath(project, path);
                target = new(path, [], project);
            }
            else
            {
                var root = ProjectAccessService.SelectRoot(directories, rootId ?? ProjectAccessService.GetRoots(directories)[0].Id);
                target = new(ProjectAccessService.ResolveRelativePath(root, path), directories);
            }

            var result = await editing.SaveAsync(GetUserId(), target, request, ct);
            return result.Conflict ? Conflict(result) : Ok(result);
        }
        catch (Exception ex) when (TryProblem(ex, out var response)) { return response; }
    }

    /// <summary>Reads staged, unstaged and untracked state on the project's device.</summary>
    [HttpGet("{projectId}/changes")]
    [HttpGet("{projectId}/threads/{threadId}/changes")]
    public async Task<IActionResult> Changes(string projectId, string? threadId, CancellationToken ct)
    {
        try
        {
            var (project, _) = await ResolveAsync(projectId, threadId, ct);
            ApplyPrivateFileHeaders();
            return Ok(project.NodeId is null ? await git.GetChangesAsync(project, ct)
                : await ReadNodeAsync<ProjectChanges>(project, ProjectCapability.Changes, new { }, ct));
        }
        catch (Exception ex) when (TryProblem(ex, out var response)) { return response; }
    }

    /// <summary>Compares the current bounded sides of a device-verified repository change.</summary>
    [HttpGet("{projectId}/change")]
    [HttpGet("{projectId}/threads/{threadId}/change")]
    public async Task<IActionResult> Change(string projectId, string? threadId, [FromQuery] ProjectChange selection, CancellationToken ct)
    {
        try
        {
            var (project, directories) = await ResolveAsync(projectId, threadId, ct);
            ApplyPrivateFileHeaders();
            return Ok(project.NodeId is null ? await git.GetChangeAsync(project, selection, directories, ct)
                : await ReadNodeAsync<ProjectChangePreview>(project, ProjectCapability.Change, new { selection }, ct));
        }
        catch (Exception ex) when (TryProblem(ex, out var response)) { return response; }
    }

    private async Task<T> ReadNodeAsync<T>(ProjectDefinition project, string operation, object arguments, CancellationToken ct)
    {
        var result = await nodes.InvokeAsync(GetUserId(), project, operation, arguments,
            [UserPermissionKeys.Projects], null, ct);
        return result.Deserialize<T>(Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions) ?? throw new InvalidDataException("The node returned no project response.");
    }

    // Invalid browser-relative paths remain request errors before any node operation is dispatched.
    private static void ValidateNodeFilePath(ProjectDefinition project, string path)
    {
        try { ProjectAccessService.ResolveRelativePath(new AllowedDirectory(project.RootPath, "project", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite), path); }
        catch (UnauthorizedAccessException ex) { throw new ArgumentException(ex.Message, nameof(path), ex); }
    }

    private async Task<(ProjectDefinition Project, IReadOnlyList<AllowedDirectory> Directories)> ResolveAsync(
        string projectId, string? threadId, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        var project = await scope.ProjectAccess.AuthorizeAsync(projectId, ct);
        if (threadId is null)
        {
            return (project, scope.ProjectAccess.ResolveRunDirectories(projectId, null));
        }

        var thread = await scope.ChatThreadRepository.GetThreadAsync(scope.UserId, threadId, ct);
        if (thread is null || thread.ProjectId != projectId) { throw new KeyNotFoundException(); }

        var directories = await scope.ProjectAccess.ResolveThreadDirectoriesAsync(thread, scope.CrewService,
            scope.AgentDefinitionStore, ct: ct);
        return (project, directories);
    }

    private bool TryProblem(Exception exception, out IActionResult response)
    {
        var status = exception switch
        {
            KeyNotFoundException or FileNotFoundException or DirectoryNotFoundException => StatusCodes.Status404NotFound,
            UnauthorizedAccessException => StatusCodes.Status403Forbidden,
            ArgumentException or InvalidOperationException or ToolFailureException => StatusCodes.Status400BadRequest,
            IOException or InvalidDataException => StatusCodes.Status503ServiceUnavailable,
            _ => 0
        };
        if (status == 0) { response = null!; return false; }

        logger.LogWarning(exception, "Project request failed for user {UserId}.", GetUserId());
        response = Problem(statusCode: status, detail: status == StatusCodes.Status404NotFound
            ? "The project or conversation is unavailable." : exception.Message);
        return true;
    }
}
