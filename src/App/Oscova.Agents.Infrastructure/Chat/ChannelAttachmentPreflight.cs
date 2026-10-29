using Microsoft.Extensions.Logging;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Hooks;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Authorizes raw channel attachment metadata before any attachment is fetched or staged.</summary>
internal static class ChannelAttachmentPreflight
{
    /// <summary>Runs the prompt hook and stages attachments only when the run may use the channel network.</summary>
    public static async Task<IReadOnlyList<ChatAttachment>> StageAfterAuthorizationAsync(
        IAgentRuntimeHookService runtimeHookService,
        RunContextAccessor runContextAccessor,
        RunContext runContext,
        PromptHookContext promptContext,
        Func<Task<List<ChatAttachment>>> stageAttachmentsAsync,
        CancellationToken ct)
    {
        await using var contextScope = runContextAccessor.SetContext(runContext).ConfigureAwait(false);
        var decision = await runtimeHookService
            .DispatchUserPromptSubmitAsync(promptContext, ct)
            .ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            throw new HookDeniedException(
                HookEventKind.UserPromptSubmit,
                toolName: null,
                decision.Message ?? "User prompt was denied by a hook.",
                decision.Reason);
        }

        if (runContext.AirGapped)
        {
            return [];
        }

        ct.ThrowIfCancellationRequested();
        return await stageAttachmentsAsync().ConfigureAwait(false);
    }

    /// <summary>Maps channel-supplied file metadata without reading the remote attachment.</summary>
    public static ChatAttachmentInfo Map(string displayName, string? mediaType, long? sizeBytes)
    {
        return new ChatAttachmentInfo
        {
            DisplayName = displayName,
            MediaType = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType,
            SizeBytes = Math.Max(0, sizeBytes ?? 0),
            Kind = ChatAttachmentLimits.KindFor(Path.GetExtension(displayName))
        };
    }

    /// <summary>Removes files staged for a channel message when the message cannot be completed.</summary>
    public static void RollbackStaged(
        IChatAttachmentService chatAttachmentService,
        ILogger logger,
        string ownerUserId,
        IReadOnlyList<ChatAttachment> staged)
    {
        foreach (var attachment in staged)
        {
            try
            {
                File.Delete(chatAttachmentService.ResolveAbsolutePath(ownerUserId, attachment));
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to remove staged channel attachment '{AttachmentId}' for user '{UserId}'.",
                    attachment.Id,
                    ownerUserId);
            }
        }
    }
}
