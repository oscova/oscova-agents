using Oscova.Agents.Conversations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Host.Artifacts;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

// Pure attachment-staging endpoint. The client uploads files here first, gets back ids,
// then sends those ids in the AG-UI run's forwardedProps so the dispatch agent can hydrate
// them into ChatAttachment records without re-uploading on the SSE call.
[ApiController]
[Authorize]
[Route("api/chat/threads/{threadId}/attachments")]
[IgnoreAntiforgeryToken]
public sealed class ChatAttachmentsController(
    IChatAttachmentService chatAttachmentService,
    UserServiceManager userServiceManager,
    ILogger<ChatAttachmentsController> logger)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpPost]
    [RequestSizeLimit(ChatAttachmentLimits.MaxRequestSizeBytes)]
    public async Task<ActionResult<IReadOnlyList<StagedAttachmentResponse>>> Stage(
        string threadId,
        [FromForm(Name = "files")] IList<IFormFile> files,
        CancellationToken ct)
    {
        if (files.Count > ChatAttachmentLimits.MaxFilesPerMessage)
        {
            return BadRequest(new { error = $"A message may include at most {ChatAttachmentLimits.MaxFilesPerMessage} attachments." });
        }

        var userId = GetUserId();
        var scope = await GetUserScopeAsync(ct);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct);
        if (thread is null)
        {
            return NotFound();
        }

        var incognito = thread.IsIncognito;

        var staged = new List<ChatAttachment>();
        try
        {
            foreach (var file in files)
            {
                await using var stream = file.OpenReadStream();
                var attachment = await chatAttachmentService.StageAsync(
                    userId,
                    threadId,
                    file.FileName,
                    file.ContentType ?? string.Empty,
                    stream,
                    file.Length,
                    incognito,
                    ct);
                staged.Add(attachment);
            }
        }
        catch (ChatAttachmentValidationException ex)
        {
            RollbackStagedFiles(userId, threadId, staged);
            return BadRequest(new { error = ex.Message });
        }
        catch
        {
            RollbackStagedFiles(userId, threadId, staged);
            throw;
        }

        return Ok(staged.ConvertAll(StagedAttachmentResponse.FromAttachment));
    }

    /// <summary>Reopens a sent attachment: previewable files render inline, other kinds download.</summary>
    [HttpGet("{attachmentId}")]
    public async Task<IActionResult> Open(string threadId, string attachmentId, CancellationToken ct)
    {
        var userId = GetUserId();
        var scope = await GetUserScopeAsync(ct);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct);
        var attachment = thread is null ? null : chatAttachmentService.Get(userId, threadId, attachmentId, thread.IsIncognito);
        if (attachment is null)
        {
            return NotFound();
        }

        var path = chatAttachmentService.ResolveAbsolutePath(userId, attachment);
        ApplyPrivateFileHeaders();
        if (ArtifactFilePresentation.OpensInBrowserTab(attachment.DisplayName))
        {
            return PhysicalFile(path, ArtifactFilePresentation.GetInlineContentType(attachment.DisplayName), enableRangeProcessing: true);
        }

        // The uploader chose the media type, so downloads use a neutral type instead of echoing it.
        return PhysicalFile(path, "application/octet-stream", attachment.DisplayName, enableRangeProcessing: true);
    }

    private void RollbackStagedFiles(string userId, string threadId, List<ChatAttachment> staged)
    {
        foreach (var attachment in staged)
        {
            try
            {
                var absolute = chatAttachmentService.ResolveAbsolutePath(userId, attachment);
                if (System.IO.File.Exists(absolute))
                {
                    System.IO.File.Delete(absolute);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to roll back staged attachment '{AttachmentId}' for user {UserId}, thread {ThreadId}.",
                    attachment.Id, userId, threadId);
            }
        }
    }
}
