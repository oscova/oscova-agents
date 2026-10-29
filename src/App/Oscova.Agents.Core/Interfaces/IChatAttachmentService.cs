using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// Stages user-supplied chat attachments on disk per user and thread.
/// Validates allowed extensions, content types, and size before persisting.
/// </summary>
public interface IChatAttachmentService
{
    /// <summary>Stores the given file under the user/thread folder and returns its metadata.</summary>
    Task<ChatAttachment> StageAsync(
        string userId,
        string threadId,
        string originalFileName,
        string contentType,
        Stream content,
        long contentLength,
        bool incognito,
        CancellationToken ct);

    /// <summary>Best-effort delete of the thread's staging folder. Failures are logged, not thrown.</summary>
    void DeleteThreadFolder(string userId, string threadId, bool incognito);

    /// <summary>Best-effort delete of every staged attachment folder for the user.</summary>
    /// <returns>False when any folder could not be removed.</returns>
    bool DeleteAllThreadFolders(string userId);

    /// <summary>Resolves the absolute staging folder for a chat thread's attachments.</summary>
    string ResolveThreadFolderPath(string userId, string threadId, bool incognito);

    /// <summary>Resolves the absolute output folder for a chat thread.</summary>
    string ResolveToolOutputFolderPath(string userId, string threadId, bool incognito);

    /// <summary>Resolves the absolute path for a staged attachment.</summary>
    string ResolveAbsolutePath(string userId, ChatAttachment attachment);

    /// <summary>
    /// Reconstructs a previously staged attachment from disk by id. Returns null when the file is missing.
    /// Used by the AG-UI dispatch path to hydrate <c>forwardedProps.attachmentIds</c> back into rich records.
    /// </summary>
    ChatAttachment? Get(string userId, string threadId, string attachmentId, bool incognito);

    /// <summary>Lists the thread-owned encrypted outputs present at a completed reply.</summary>
    IReadOnlyList<string> GetToolOutputFileNames(string userId, string threadId, bool incognito);

    /// <summary>Copies a captured prefix's files and returns references to rebind to the child.</summary>
    Task<IReadOnlyDictionary<string, string>> CopyThreadFilesAsync(string userId, string sourceThreadId,
        ChatThread child, IReadOnlyList<string> toolOutputFileNames,
        Oscova.Agents.Secrets.IContentProtector protector, CancellationToken ct = default);
}
