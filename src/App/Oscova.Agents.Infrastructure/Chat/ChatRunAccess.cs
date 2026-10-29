using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Workspace;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// Builds the plain directory entries that belong to a chat thread run.
/// </summary>
public static class ChatRunAccess
{
    /// <summary>
    /// Label of the read-only chat-attachment directory. Shared so the mount label and the
    /// path printed in the attachment manifest resolve to the same agent-facing <c>/mnt/&lt;label&gt;</c>.
    /// </summary>
    public const string AttachmentDirectoryLabel = "chat attachments for this thread";

    /// <summary>
    /// Returns the standard directories and their access modes for the given chat thread.
    /// </summary>
    public static IReadOnlyList<AllowedDirectory> BuildStandardRunDirectories(
        IChatAttachmentService chatAttachmentService,
        string userId,
        ChatThread thread,
        ChatDisplayMessage? currentTurnMessage = null)
    {
        AllowedDirectory output = ToolOutputStore.CreateRunDirectory(
            chatAttachmentService.ResolveToolOutputFolderPath(userId, thread.Id, thread.IsIncognito));
        var visualizationPath = chatAttachmentService.ResolveVisualizationFolderPath(userId, thread.Id, thread.IsIncognito);
        // File writers require an existing grant root, regardless of the agent's current skill selection.
        Directory.CreateDirectory(visualizationPath);
        var visualizations = new AllowedDirectory(visualizationPath, ChatVisualizationFiles.DirectoryLabel,
            DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite);
        var attachmentDirectory = GetAttachmentDirectory(chatAttachmentService, userId, thread, currentTurnMessage);
        if (attachmentDirectory is null)
        {
            return [output, visualizations];
        }

        return
        [
            attachmentDirectory,
            output,
            visualizations
        ];
    }

    /// <summary>Reconstructs the thread's read-only attachment grant without creating run directories.</summary>
    public static AllowedDirectory? GetAttachmentDirectory(IChatAttachmentService chatAttachmentService,
        string userId, ChatThread thread, ChatDisplayMessage? currentTurnMessage = null)
    {
        return thread.DisplayMessages.Any(message => message.Attachments.Count > 0) ||
               currentTurnMessage?.Attachments.Count > 0
            ? new AllowedDirectory(chatAttachmentService.ResolveThreadFolderPath(userId, thread.Id, thread.IsIncognito),
                AttachmentDirectoryLabel, DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly,
                PathHelper.ToExtraMountAgentPath(AttachmentDirectoryLabel))
            : null;
    }
}
