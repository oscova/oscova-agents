using Oscova.Agents.Conversations;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// Disk-backed implementation of <see cref="IChatAttachmentService"/>.
/// Stages files under <c>app_data/users/{userId}/chat/attachments/{threadId}/</c>.
/// </summary>
public sealed partial class ChatAttachmentService(
    IUserDirectoryService userDirectoryService,
    ILogger<ChatAttachmentService> logger) : IChatAttachmentService
{
    private const string ChatAttachmentsFolder = "chat";
    private const string AttachmentsSubfolder = "attachments";
    private const string IncognitoRootFolder = "oscova-incognito";
    private const int AttachmentIdLength = 12;
    private const int AttachmentIdAllocationAttempts = 8;

    /// <summary>Gets this process's staging root for incognito attachments, outside the user's data directory.</summary>
    public static string IncognitoRoot => Path.Combine(
        Path.GetTempPath(),
        IncognitoRootFolder,
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    // Explicit set (not Path.GetInvalidFileNameChars) so the sanitizer behaves the same on every OS.
    private static readonly HashSet<char> InvalidFileNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    public async Task<ChatAttachment> StageAsync(
        string userId,
        string threadId,
        string originalFileName,
        string contentType,
        Stream content,
        long contentLength,
        bool incognito,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            throw new ChatAttachmentValidationException("Attachment is missing a file name.");
        }

        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrEmpty(extension) ||
            !ChatAttachmentLimits.AllowedExtensions.TryGetValue(extension, out var allowedTypes))
        {
            throw new ChatAttachmentValidationException(
                $"File type '{extension}' is not supported (in '{originalFileName}').");
        }

        // Extension is the gatekeeper. Browsers often send empty or generic content types
        // for text formats like .md; fall back to the first allowed type in that case.
        // Only reject when the browser supplied a specific type that contradicts the extension.
        var effectiveContentType = contentType;
        if (string.IsNullOrWhiteSpace(effectiveContentType) ||
            string.Equals(effectiveContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            effectiveContentType = allowedTypes[0];
        }
        else if (!allowedTypes.Contains(effectiveContentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ChatAttachmentValidationException(
                $"Content type '{contentType}' does not match extension '{extension}' for '{originalFileName}'.");
        }

        if (contentLength > ChatAttachmentLimits.MaxFileBytes)
        {
            throw new ChatAttachmentValidationException(
                $"Attachment '{originalFileName}' exceeds the 10 MB size limit.");
        }

        if (!incognito)
        {
            await userDirectoryService.EnsureUserDirectoryExistsAsync(userId).ConfigureAwait(false);
        }

        var threadFolderAbsolute = ResolveThreadFolderPath(userId, threadId, incognito);
        var threadFolderRelative = BuildThreadFolderRelative(threadId, incognito);
        Directory.CreateDirectory(threadFolderAbsolute);

        var safeName = BuildSafeFileName(originalFileName);
        var (attachmentId, tempPath, output) = ReserveAttachmentId(threadFolderAbsolute);
        string storedFileName;
        string finalPath;
        long written = 0;
        try
        {
            await using (output.ConfigureAwait(false))
            {
                storedFileName = $"{attachmentId}-{safeName}";
                finalPath = Path.Combine(threadFolderAbsolute, storedFileName);
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > ChatAttachmentLimits.MaxFileBytes)
                    {
                        throw new ChatAttachmentValidationException(
                            $"Attachment '{originalFileName}' exceeds the 10 MB size limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return new ChatAttachment
        {
            Id = attachmentId,
            DisplayName = safeName,
            StoredFileName = storedFileName,
            MediaType = effectiveContentType,
            SizeBytes = written,
            Kind = ChatAttachmentLimits.KindFor(extension),
            RelativePath = Path.Combine(threadFolderRelative, storedFileName),
            IsEphemeral = incognito
        };
    }

    private (string Id, string TempPath, FileStream Stream) ReserveAttachmentId(string threadFolder)
    {
        for (var attempt = 0; attempt < AttachmentIdAllocationAttempts; attempt++)
        {
            var id = RandomNumberGenerator.GetHexString(AttachmentIdLength, lowercase: true);
            var tempPath = Path.Combine(threadFolder, id + ".tmp");
            FileStream? stream = null;

            try
            {
                // Reserve the scoped ID before consuming a potentially non-seekable upload stream.
                stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (!Directory.EnumerateFiles(threadFolder, $"{id}-*").Any())
                {
                    return (id, tempPath, stream);
                }
            }
            catch (IOException) when (stream is null && File.Exists(tempPath))
            {
                continue;
            }
            catch
            {
                stream?.Dispose();
                if (stream is not null)
                {
                    TryDelete(tempPath);
                }

                throw;
            }

            stream.Dispose();
            TryDelete(tempPath);
        }

        throw new IOException("A unique attachment id could not be allocated.");
    }

    // The incognito root already ends at the user folder, so the relative path starts at the thread.
    private static string BuildThreadFolderRelative(string threadId, bool incognito)
    {
        return incognito
            ? threadId
            : Path.Combine(ChatAttachmentsFolder, AttachmentsSubfolder, threadId);
    }

    public void DeleteThreadFolder(string userId, string threadId, bool incognito)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        foreach (string folder in new[]
                 {
                     ResolveThreadFolderPath(userId, threadId, incognito),
                     ResolveToolOutputFolderPath(userId, threadId, incognito),
                     this.ResolveVisualizationFolderPath(userId, threadId, incognito)
                 })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete chat thread folder {Folder} for thread {ThreadId}.", folder, threadId);
            }
        }
    }

    public string ResolveThreadFolderPath(string userId, string threadId, bool incognito)
    {
        return ResolveContainedThreadFolder(ResolveAttachmentsRoot(userId, incognito), threadId);
    }

    /// <inheritdoc />
    public string ResolveToolOutputFolderPath(string userId, string threadId, bool incognito) =>
        ResolveContainedThreadFolder(ResolveToolOutputRoot(userId, incognito), threadId);

    private string ResolveToolOutputRoot(string userId, bool incognito)
    {
        if (!incognito)
        {
            return Path.Combine(userDirectoryService.GetUserDirectoryPath(userId), ChatAttachmentsFolder, ToolOutputStore.FolderName);
        }

        var candidate = Path.GetFullPath(Path.Combine(IncognitoRoot, userId, ToolOutputStore.FolderName));
        if (!PathHelper.IsPathWithinDirectory(candidate, IncognitoRoot))
        {
            throw new ChatAttachmentValidationException($"Invalid user id '{userId}'.");
        }

        return candidate;
    }

    private string ResolveAttachmentsRoot(string userId, bool incognito)
    {
        if (!incognito)
        {
            var userRoot = userDirectoryService.GetUserDirectoryPath(userId);
            return Path.Combine(userRoot, ChatAttachmentsFolder, AttachmentsSubfolder);
        }

        var candidate = Path.GetFullPath(Path.Combine(IncognitoRoot, userId));
        if (!PathHelper.IsPathWithinDirectory(candidate, IncognitoRoot))
        {
            throw new ChatAttachmentValidationException($"Invalid user id '{userId}'.");
        }

        return candidate;
    }

    // Guards against rooted or traversal-laden thread IDs resolving outside the attachments root.
    private static string ResolveContainedThreadFolder(string attachmentsRoot, string threadId)
    {
        if (!PathHelper.IsSafePathSegment(threadId) ||
            !PathHelper.TryResolveContainedPath(attachmentsRoot, threadId, out var candidate))
        {
            throw new ChatAttachmentValidationException($"Invalid thread id '{threadId}'.");
        }

        return candidate;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetToolOutputFileNames(string userId, string threadId, bool incognito)
    {
        var folder = ResolveToolOutputFolderPath(userId, threadId, incognito);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*" + Oscova.Agents.Secrets.EncryptedFileContent.FileExtension)
                .Select(path => Path.GetFileName(path)!).ToArray()
            : [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> CopyThreadFilesAsync(string userId,
        string sourceThreadId, ChatThread child, IReadOnlyList<string> toolOutputFileNames,
        Oscova.Agents.Secrets.IContentProtector protector, CancellationToken ct = default)
    {
        var sourceAttachments = ResolveThreadFolderPath(userId, sourceThreadId, child.IsIncognito);
        var childAttachments = ResolveThreadFolderPath(userId, child.Id, child.IsIncognito);
        var sourceOutputs = ResolveToolOutputFolderPath(userId, sourceThreadId, child.IsIncognito);
        var childOutputs = ResolveToolOutputFolderPath(userId, child.Id, child.IsIncognito);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var outputs = new List<(string Source, string Destination)>();

        string ResolveFile(string folder, string name)
        {
            if (!PathHelper.IsSafePathSegment(name) ||
                !PathHelper.TryResolveContainedPath(folder, name, out var path))
            {
                throw new ChatAttachmentValidationException("A captured chat file is outside its owned folder.");
            }

            return path;
        }

        void MapPath(string original, string replacement)
        {
            paths[original] = replacement;
            paths[PathHelper.ToSlashSeparatedRelativePath(original)] =
                PathHelper.ToSlashSeparatedRelativePath(replacement);
        }

        var sourceVisualizations = this.ResolveVisualizationFolderPath(userId, sourceThreadId, child.IsIncognito);
        var childVisualizations = this.ResolveVisualizationFolderPath(userId, child.Id, child.IsIncognito);
        // Receipts are thread-relative, while retained authoring calls and instructions contain full paths.
        MapPath(sourceVisualizations, childVisualizations);
        foreach (var segment in child.DisplayMessages.Where(message => message.From == MessageOwner.Bot).SelectMany(message => message.Segments))
        {
            if (!ChatVisualizationService.TryRead(segment, out var receipt)) { continue; }

            if (!ChatVisualizationService.IsAuthentic(userId, sourceThreadId, receipt!, protector)) { continue; }

            var childReceipt = ChatVisualizationService.CreateReceipt(userId, child.Id, receipt!.FileName, protector);
            paths[receipt.Proof] = childReceipt.Proof;
            var original = ResolveFile(sourceVisualizations, receipt!.FileName);
            var replacement = ResolveFile(childVisualizations, receipt.FileName);
            if (!File.Exists(original) || File.Exists(replacement)) { continue; }

            Directory.CreateDirectory(childVisualizations);
            var files = new WorkspaceFileAccess(() =>
                [new AllowedDirectory(sourceVisualizations, ChatVisualizationFiles.DirectoryLabel,
                    DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)]);
            using var input = files.OpenRead(original);
            using var output = File.Create(replacement);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }

        foreach (var message in child.DisplayMessages)
        {
            var attachments = new List<ChatAttachment>();
            foreach (var attachment in message.Attachments)
            {
                ct.ThrowIfCancellationRequested();
                var canonical = Get(userId, sourceThreadId, attachment.Id, child.IsIncognito)
                    ?? throw new FileNotFoundException("A captured chat attachment is unavailable.");
                if (canonical.StoredFileName != attachment.StoredFileName ||
                    canonical.RelativePath != attachment.RelativePath)
                {
                    throw new ChatAttachmentValidationException("A captured attachment does not belong to its source chat.");
                }

                var original = ResolveFile(sourceAttachments, attachment.StoredFileName);
                var replacement = ResolveFile(childAttachments, attachment.StoredFileName);
                if (!paths.ContainsKey(original))
                {
                    Directory.CreateDirectory(childAttachments);
                    File.Copy(original, replacement);
                    MapPath(original, replacement);
                }

                var relative = Path.Combine(BuildThreadFolderRelative(child.Id, child.IsIncognito), attachment.StoredFileName);
                MapPath(attachment.RelativePath, relative);
                attachments.Add(new ChatAttachment
                {
                    Id = attachment.Id,
                    DisplayName = attachment.DisplayName,
                    StoredFileName = attachment.StoredFileName,
                    MediaType = attachment.MediaType,
                    SizeBytes = attachment.SizeBytes,
                    Kind = attachment.Kind,
                    RelativePath = relative,
                    IsEphemeral = child.IsIncognito
                });
            }

            message.Attachments = attachments;
        }

        foreach (var fileName in toolOutputFileNames)
        {
            var original = ResolveFile(sourceOutputs, fileName);
            var replacement = ResolveFile(childOutputs, fileName);
            MapPath(original, replacement);
            outputs.Add((original, replacement));
        }

        if (outputs.Count > 0) { Directory.CreateDirectory(childOutputs); }

        foreach (var output in outputs)
        {
            var text = await Oscova.Agents.Secrets.EncryptedFileContent.ReadAllTextAsync(
                userId, output.Source, protector, ct).ConfigureAwait(false)
                ?? throw new FileNotFoundException("A captured tool output is unavailable.");
            await Oscova.Agents.Secrets.EncryptedFileContent.WriteAllTextAsync(userId, output.Destination,
                RebindFilePaths(text, paths), protector, ct).ConfigureAwait(false);
        }

        return paths;
    }

    /// <summary>Rebinds known chat-owned file references without changing other serialized state.</summary>
    public static T RebindFilePaths<T>(T value, IReadOnlyDictionary<string, string> paths)
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var replacements = paths.OrderByDescending(pair => pair.Key.Length).ToArray();
        System.Text.Json.Nodes.JsonNode? Rewrite(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                foreach (var pair in replacements) { text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal); }

                return System.Text.Json.Nodes.JsonValue.Create(text);
            }

            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                foreach (var pair in obj.ToArray()) { obj[pair.Key] = Rewrite(pair.Value); }
            }
            else if (node is System.Text.Json.Nodes.JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    // Array items must be detached before assigning their rewritten node.
                    var item = array[index];
                    array[index] = null;
                    array[index] = Rewrite(item);
                }
            }

            return node;
        }

        var rewritten = Rewrite(System.Text.Json.JsonSerializer.SerializeToNode(value, options))
            ?? throw new System.Text.Json.JsonException("The captured chat state is empty.");
        return System.Text.Json.JsonSerializer.Deserialize<T>(rewritten, options)!;
    }

    public bool DeleteAllThreadFolders(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return true;
        }

        var succeeded = true;
        foreach (var incognito in new[] { false, true })
        {
            foreach (string root in new[] { ResolveAttachmentsRoot(userId, incognito), ResolveToolOutputRoot(userId, incognito),
                         Path.GetDirectoryName(this.ResolveVisualizationFolderPath(userId, "root", incognito))! })
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    succeeded = false;
                    logger.LogWarning(ex, "Failed to delete chat folder {Folder} for user {UserId}.", root, userId);
                }
            }
        }

        return succeeded;
    }

    public ChatAttachment? Get(string userId, string threadId, string attachmentId, bool incognito)
    {
        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(threadId) ||
            string.IsNullOrWhiteSpace(attachmentId))
        {
            return null;
        }

        // attachmentId flows from client-controlled forwardedProps.attachmentIds and is fed
        // to Directory.GetFiles as a glob pattern; accept only the shape StageAsync produces.
        if (!IsAttachmentId(attachmentId))
        {
            logger.LogWarning("Rejected attachment lookup with invalid id '{AttachmentId}'.", attachmentId);
            return null;
        }

        try
        {
            var threadFolderAbsolute = ResolveThreadFolderPath(userId, threadId, incognito);
            if (!Directory.Exists(threadFolderAbsolute))
            {
                return null;
            }

            var matches = Directory.GetFiles(threadFolderAbsolute, $"{attachmentId}-*");
            if (matches.Length != 1)
            {
                if (matches.Length > 1)
                {
                    logger.LogWarning(
                        "Rejected ambiguous attachment lookup for id '{AttachmentId}' in thread {ThreadId}.",
                        attachmentId,
                        threadId);
                }

                return null;
            }

            var storedPath = matches[0];
            var storedFileName = Path.GetFileName(storedPath);
            var safeName = storedFileName.Length > attachmentId.Length + 1
                ? storedFileName[(attachmentId.Length + 1)..]
                : storedFileName;
            var extension = Path.GetExtension(safeName);

            if (string.IsNullOrEmpty(extension) ||
                !ChatAttachmentLimits.AllowedExtensions.TryGetValue(extension, out var allowedTypes))
            {
                logger.LogWarning(
                    "Staged attachment '{AttachmentId}' has unsupported extension '{Extension}'.",
                    attachmentId,
                    extension);
                return null;
            }

            var info = new FileInfo(storedPath);
            var threadFolderRelative = BuildThreadFolderRelative(threadId, incognito);
            return new ChatAttachment
            {
                Id = attachmentId,
                DisplayName = safeName,
                StoredFileName = storedFileName,
                MediaType = allowedTypes[0],
                SizeBytes = info.Length,
                Kind = ChatAttachmentLimits.KindFor(extension),
                RelativePath = Path.Combine(threadFolderRelative, storedFileName),
                IsEphemeral = incognito
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to hydrate staged attachment '{AttachmentId}' for thread {ThreadId}.", attachmentId, threadId);
            return null;
        }
    }

    private static bool IsAttachmentId(string value) =>
        value.Length == AttachmentIdLength &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public string ResolveAbsolutePath(string userId, ChatAttachment attachment)
    {
        var root = attachment.IsEphemeral
            ? ResolveAttachmentsRoot(userId, incognito: true)
            : userDirectoryService.GetUserDirectoryPath(userId);
        var resolved = Path.GetFullPath(Path.Combine(root, attachment.RelativePath));
        if (!PathHelper.IsPathWithinDirectory(resolved, root))
        {
            throw new ChatAttachmentValidationException("Attachment path resolves outside the attachment root.");
        }

        return resolved;
    }

    /// <summary>Removes incognito staging folders left behind by processes that are no longer running.</summary>
    public static void WipeIncognitoRoot()
    {
        Exception? deletionFailure = null;
        void TryDeleteFolder(string folder)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                deletionFailure ??= ex;
            }
        }

        // This process owns nothing yet at startup, so a folder under our own id is always stale.
        if (Directory.Exists(IncognitoRoot))
        {
            TryDeleteFolder(IncognitoRoot);
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), IncognitoRootFolder);
        if (Directory.Exists(stagingRoot))
        {
            foreach (var folder in Directory.EnumerateDirectories(stagingRoot))
            {
                // A folder named after a live process belongs to another instance still using it.
                if (!int.TryParse(Path.GetFileName(folder), CultureInfo.InvariantCulture, out var processId) ||
                    !IsProcessRunning(processId))
                {
                    TryDeleteFolder(folder);
                }
            }
        }

        if (deletionFailure is not null)
        {
            throw new AggregateException("Failed to remove one or more incognito staging folders.", deletionFailure);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string BuildSafeFileName(string originalFileName)
    {
        var fileName = Path.GetFileName(originalFileName);
        var builder = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            if (char.IsControl(ch) || InvalidFileNameChars.Contains(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        var cleaned = WhitespaceRegex().Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
        if (string.IsNullOrEmpty(cleaned))
        {
            cleaned = "attachment";
        }

        const int maxLength = 128;
        if (cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        var ext = Path.GetExtension(cleaned);
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        var room = Math.Max(1, maxLength - ext.Length);
        return stem[..Math.Min(stem.Length, room)] + ext;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup; log so permission or lock issues are diagnosable.
            logger.LogWarning(ex, "Failed to delete staged attachment file '{Path}' during cleanup.", path);
        }
    }

    [GeneratedRegex(@"\s+", RegexOptions.None, 100)]
    private static partial Regex WhitespaceRegex();
}
