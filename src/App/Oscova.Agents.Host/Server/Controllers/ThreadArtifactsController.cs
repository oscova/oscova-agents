using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Host.Artifacts;
using Oscova.Agents.Host.Server.Services;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Secrets;
using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Harness;
using System.Text;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Streams current thread-attributed files through authenticated opaque artifact routes.
/// </summary>
[ApiController]
[Authorize]
[Route("api/chat/threads/{threadId}/artifacts")]
[IgnoreAntiforgeryToken]
public sealed class ThreadArtifactsController(
    ArtifactFileAccessService artifactFileAccessService,
    UserServiceManager userServiceManager,
    IContentProtector protector,
    WorkspaceFileEditingService editing,
    ILogger<ThreadArtifactsController> logger)
    : UserScopeControllerBase(userServiceManager)
{
    /// <summary>Opens a genuine artifact as complete bounded editable text.</summary>
    [HttpGet("{artifactId}/edit")]
    public async Task<IActionResult> Editable(string threadId, string artifactId, CancellationToken ct)
    {
        try
        {
            ApplyPrivateFileHeaders();
            var target = await artifactFileAccessService.ResolveTargetAsync(GetUserId(), threadId, artifactId, ct);
            if (target is null) { return NotFound(); }

            return Ok(await editing.ReadAsync(GetUserId(), target.File, ct));
        }
        catch (Exception ex) when (TryEditingProblem(ex, out var response)) { return response; }
    }

    /// <summary>Saves to the genuine artifact's original file after current access and revision checks.</summary>
    [HttpPut("{artifactId}/edit")]
    public async Task<IActionResult> SaveEditable(string threadId, string artifactId,
        SaveProjectFileRequest request, CancellationToken ct)
    {
        try
        {
            ApplyPrivateFileHeaders();
            var target = await artifactFileAccessService.ResolveTargetAsync(GetUserId(), threadId, artifactId, ct);
            if (target is null) { return NotFound(); }

            var result = await editing.SaveAsync(GetUserId(), target.File, request, ct);
            return result.Conflict ? Conflict(result) : Ok(result);
        }
        catch (Exception ex) when (TryEditingProblem(ex, out var response)) { return response; }
    }

    private bool TryEditingProblem(Exception ex, out IActionResult response)
    {
        var status = ex switch
        {
            FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => StatusCodes.Status404NotFound,
            UnauthorizedAccessException => StatusCodes.Status403Forbidden,
            ArgumentException or InvalidOperationException or ToolFailureException => StatusCodes.Status400BadRequest,
            IOException => StatusCodes.Status503ServiceUnavailable,
            _ => 0
        };
        if (status == 0) { response = null!; return false; }

        logger.LogWarning(ex, "Artifact editing failed for user {UserId}.", GetUserId());
        response = Problem(statusCode: status, detail: "The file could not be opened or saved. Check file access and the project's device, then try again.");
        return true;
    }

    /// <summary>
    /// Streams an allowlisted inline preview for a genuine current thread artifact.
    /// </summary>
    [HttpGet("{artifactId}/content")]
    public async Task<IActionResult> Content(
        string threadId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var descriptor = await artifactFileAccessService
            .ResolveAsync(userId, threadId, artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (descriptor is null)
        {
            return NotFound();
        }

        if (descriptor.Artifact.PreviewKind == ArtifactPreviewKind.DownloadOnly)
        {
            descriptor.Stream.Dispose();
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var encrypted = EncryptedFileContent.IsEncryptedPath(descriptor.Artifact.FileName);
        // The stored envelope is larger than the plaintext preview limit.
        if (descriptor.Artifact.PreviewKind is ArtifactPreviewKind.Markdown or ArtifactPreviewKind.Text &&
            descriptor.Length > (encrypted ? UserFileLimits.MaxEncryptedEditSizeBytes : UserFileLimits.MaxEditSizeBytes))
        {
            descriptor.Stream.Dispose();
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        ApplyPrivateFileHeaders();
        if (encrypted)
        {
            using var reader = new StreamReader(descriptor.Stream);
            var storedContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var protection = await editing.GetProtectionAsync(userId, descriptor.File, cancellationToken).ConfigureAwait(false);
            EncryptedFileContent.TryDecrypt(protection.KeyOwner, descriptor.Artifact.FileName, storedContent, protector,
                out var visibleContent, protection.Purpose);
            if (Encoding.UTF8.GetByteCount(visibleContent) > UserFileLimits.MaxEditSizeBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            return Content(visibleContent, descriptor.InlineContentType);
        }

        return new FileStreamResult(descriptor.Stream, descriptor.InlineContentType)
        {
            EnableRangeProcessing = true
        };
    }

    /// <summary>
    /// Downloads a genuine current thread artifact using its current filename.
    /// </summary>
    [HttpGet("{artifactId}/download")]
    [HttpHead("{artifactId}/download")]
    public async Task<IActionResult> Download(
        string threadId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        var descriptor = await artifactFileAccessService
            .ResolveAsync(GetUserId(), threadId, artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (descriptor is null)
        {
            return NotFound();
        }

        ApplyPrivateFileHeaders();
        return new FileStreamResult(descriptor.Stream, descriptor.DownloadContentType)
        {
            EnableRangeProcessing = true,
            FileDownloadName = descriptor.Artifact.FileName
        };
    }
}
