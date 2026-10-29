using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Utils;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Central allowlist and size limit for chat attachments.
/// Single source of truth — Phase 2 extensions (e.g. PDFs) edit this file only.
/// </summary>
public static class ChatAttachmentLimits
{
    public const long MaxFileBytes = 10 * ByteSize.OneMegabyte;
    public const long MaxRequestSizeBytes = 200 * ByteSize.OneMegabyte;
    public const int MaxFilesPerMessage = 50;

    /// <summary>Validates reference metadata only. File tools enforce access when the agent reads the original.</summary>
    public static bool AreWorkspaceFilesValid(IReadOnlyList<WorkspaceFileReference>? files) =>
        files?.Count <= MaxFilesPerMessage && files.All(file =>
            file is not null && !string.IsNullOrWhiteSpace(file.Name) &&
            !string.IsNullOrWhiteSpace(file.AgentPath) && !string.IsNullOrWhiteSpace(file.Source));

    public static readonly IReadOnlyDictionary<string, string[]> AllowedExtensions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = ["image/png"],
            [".jpg"] = ["image/jpeg"],
            [".jpeg"] = ["image/jpeg"],
            [".gif"] = ["image/gif"],
            [".webp"] = ["image/webp"],
            [".txt"] = ["text/plain"],
            [".md"] = ["text/markdown", "text/plain"],
            [".json"] = ["application/json", "text/plain"],
            [".csv"] = ["text/csv", "text/plain"],
            [".log"] = ["text/plain"],
            [".xml"] = ["application/xml", "text/xml"],
            [".yaml"] = ["application/yaml", "text/yaml", "text/plain"],
            [".yml"] = ["application/yaml", "text/yaml", "text/plain"],
            [".pdf"] = ["application/pdf"]
        };

    public static AttachmentKind KindFor(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => AttachmentKind.Image,
            ".pdf" => AttachmentKind.Pdf,
            _ => AttachmentKind.Text
        };
}
