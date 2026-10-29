using Microsoft.AspNetCore.StaticFiles;
using Oscova.Agents.Definitions;
using Oscova.Agents.Host.Artifacts;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;
using Oscova.Agents.Harness;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Chat;

namespace Oscova.Agents.Host.Server.Services;

/// <summary>
/// Resolves an opaque thread artifact to its current authorized host file without exposing the host path to the browser.
/// </summary>
public sealed class ArtifactFileAccessService(
    UserServiceManager userServiceManager,
    IAgentDefinitionRepository agentDefinitionRepository,
    IChatAttachmentService chatAttachmentService)
{
    private static readonly FileExtensionContentTypeProvider DownloadContentTypes = new();

    /// <summary>Resolves an owned artifact only within its authorized execution environment.</summary>
    public async Task<ArtifactFileDescriptor?> ResolveAsync(
        string userId, string threadId, string artifactId, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveTargetAsync(userId, threadId, artifactId, cancellationToken).ConfigureAwait(false);
        if (resolved is null) { return null; }

        var (artifact, target) = resolved;
        try
        {
            var stream = await OpenReadAsync(userId, target, cancellationToken).ConfigureAwait(false);
            var downloadType = DownloadContentTypes.TryGetContentType(artifact.FileName, out var contentType)
                ? contentType : "application/octet-stream";
            return new(artifact, stream, stream.Length, ArtifactFilePresentation.GetInlineContentType(artifact.FileName), downloadType,
                target);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or ToolFailureException)
        { return null; }
    }

    /// <summary>Streams an authorized workspace target from its owning device without staging a copy.</summary>
    public async Task<Stream> OpenReadAsync(string userId, WorkspaceFileTarget target, CancellationToken cancellationToken)
    {
        if (target.NodeProject is not { } project)
        {
            return new WorkspaceFileAccess(() => target.Directories).OpenRead(target.Path);
        }

        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, cancellationToken).ConfigureAwait(false);
        var metadata = await userScope.ProjectAccess.ReadNodeFileAsync(project.Id, target.Path, 0, 0,
            cancellationToken).ConfigureAwait(false);
        return new NodeArtifactStream(metadata, (offset, count, ct) =>
            userScope.ProjectAccess.ReadNodeFileAsync(project.Id, target.Path, offset, count, ct));
    }

    /// <summary>Reconstructs the genuine artifact's current grants for both preview and replacement.</summary>
    public async Task<ArtifactFileTarget?> ResolveTargetAsync(
        string userId, string threadId, string artifactId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(threadId) ||
            string.IsNullOrWhiteSpace(artifactId)) { return null; }

        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, cancellationToken).ConfigureAwait(false);
        var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, threadId, cancellationToken)
            .ConfigureAwait(false);
        if (thread is null) { return null; }

        var artifact = ThreadArtifactProjector.Project(thread.DisplayMessages, thread.ProjectId is not null).Artifacts
            .FirstOrDefault(candidate => string.Equals(candidate.Id, artifactId, StringComparison.Ordinal));
        if (artifact is null) { return null; }

        var artifactAgentId = string.IsNullOrWhiteSpace(artifact.AgentId) ? thread.AgentId : artifact.AgentId;
        AgentDefinition? agent = string.IsNullOrWhiteSpace(artifactAgentId)
            ? await agentDefinitionRepository.GetMainAgentAsync(userId, cancellationToken).ConfigureAwait(false)
            : await agentDefinitionRepository.GetAgentAsync(userId, artifactAgentId, cancellationToken).ConfigureAwait(false);
        if (agent is null) { return null; }

        var path = artifact.CanonicalPath ?? artifact.AgentPath;
        try
        {
            if (artifact.Environment == "project")
            {
                if (thread.ProjectId is null) { return null; }

                var project = await userScope.ProjectAccess.AuthorizeAsync(thread.ProjectId, cancellationToken)
                    .ConfigureAwait(false);
                if (project.NodeId is not null)
                {
                    return new(artifact, new(path, [], project));
                }
            }

            var directories = await userScope.ProjectAccess.ResolveThreadDirectoriesAsync(
                thread, userScope.CrewService, agentDefinitionRepository, agent.Id, cancellationToken).ConfigureAwait(false);
            // Uploaded originals retain the same thread-owned read grant outside an active run.
            if (ChatRunAccess.GetAttachmentDirectory(chatAttachmentService, userId, thread) is { } attachments)
            {
                directories = [.. directories, attachments];
            }

            var allowedDirectories = userScope.ProjectAccess.ResolveFileDirectories(
                directories, thread.ProjectId, artifact.Environment == "workspace");
            // Older threads name the own folder by its retired root.
            var hostPath = PathHelper.ResolveAuthorizedHostPath(
                PathHelper.MapRetiredSelfAgentPath(path), allowedDirectories, WorkspacePathAccess.Read);
            return new(artifact, new(hostPath, allowedDirectories));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or ToolFailureException)
        {
            return null;
        }
    }
}

/// <summary>Retains the attributed artifact alongside its server-authorized file destination.</summary>
public sealed record ArtifactFileTarget(ThreadArtifact Artifact, WorkspaceFileTarget File);

/// <summary>
/// Carries an authorized open stream and response metadata for one projected artifact.
/// </summary>
public sealed record ArtifactFileDescriptor(
    ThreadArtifact Artifact,
    Stream Stream,
    long Length,
    string InlineContentType,
    string DownloadContentType,
    WorkspaceFileTarget File);
