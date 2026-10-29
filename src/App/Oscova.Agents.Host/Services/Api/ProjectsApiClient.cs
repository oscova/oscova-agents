using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Constants;

namespace Oscova.Agents.Host.Services.Api;

/// <summary>Uses the authenticated project and conversation file endpoints from the dashboard.</summary>
public sealed class ProjectsApiClient(HttpClient httpClient) : ApiClientBase
{
    /// <summary>Loads the user's saved project catalog.</summary>
    public Task<IReadOnlyList<ProjectDefinition>> GetProjectsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ProjectDefinition>>(ApiRoutes.Projects.Catalog, ct);

    /// <summary>Saves one existing folder through server-side authorization.</summary>
    public async Task<ProjectDefinition> CreateAsync(CreateProjectRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.Projects.Catalog, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<ProjectDefinition>(ct).ConfigureAwait(false))!;
    }

    /// <summary>Removes a saved reference while retaining its chats and physical files.</summary>
    public async Task RemoveAsync(string projectId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Projects.Project(projectId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Lists the server-authorized folders of a conversation or the selected agent.</summary>
    public Task<IReadOnlyList<ProjectFileRoot>> GetRootsAsync(string? projectId, string? threadId, CancellationToken ct, string? agentId = null) =>
        GetAsync<IReadOnlyList<ProjectFileRoot>>(ApiRoutes.Projects.Read(projectId, threadId, "roots", agentId), ct);

    /// <summary>Lists bounded direct children of the selected root.</summary>
    public Task<ProjectFileListing> ListFilesAsync(string? projectId, string? threadId, string rootId, string path, CancellationToken ct, string? agentId = null) =>
        GetAsync<ProjectFileListing>($"{ApiRoutes.Projects.Read(projectId, threadId, "files", agentId)}?rootId={Uri.EscapeDataString(rootId)}&path={Uri.EscapeDataString(path)}", ct);

    /// <summary>Loads bounded text from the selected root.</summary>
    public Task<ProjectFilePreview> ReadFileAsync(string? projectId, string? threadId, string rootId, string path, CancellationToken ct, string? agentId = null) =>
        GetAsync<ProjectFilePreview>($"{ApiRoutes.Projects.Read(projectId, threadId, "file", agentId)}?rootId={Uri.EscapeDataString(rootId)}&path={Uri.EscapeDataString(path)}", ct);

    /// <summary>Replaces existing selected text only when its stored revision still matches.</summary>
    public Task<ProjectFileSaveResult> SaveFileAsync(string? projectId, string? threadId, string rootId,
        string path, SaveProjectFileRequest request, CancellationToken ct, string? agentId = null) =>
        SaveTextAsync(httpClient, $"{ApiRoutes.Projects.Read(projectId, threadId, "file", agentId)}?rootId={Uri.EscapeDataString(rootId)}&path={Uri.EscapeDataString(path)}", request, ct);

    /// <summary>Loads repository-wide change status.</summary>
    public Task<ProjectChanges> GetChangesAsync(string projectId, string? threadId, CancellationToken ct) =>
        GetAsync<ProjectChanges>(ApiRoutes.Projects.Read(projectId, threadId, "changes"), ct);

    /// <summary>Loads the current sides of a selected repository change.</summary>
    public Task<ProjectChangePreview> GetChangeAsync(string projectId, string? threadId, ProjectChange entry, CancellationToken ct) =>
        GetAsync<ProjectChangePreview>($"{ApiRoutes.Projects.Read(projectId, threadId, "change")}?path={Uri.EscapeDataString(entry.Path)}&oldPath={Uri.EscapeDataString(entry.OldPath ?? string.Empty)}&status={Uri.EscapeDataString(entry.Status)}&group={Uri.EscapeDataString(entry.Group)}", ct);

    private async Task<T> GetAsync<T>(string route, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(route, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false))!;
    }
}
