using System.Text;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>
/// Builds the representative pending-attachment manifest used by chat context preflight checks.
/// </summary>
public sealed class ChatAttachmentManifestEstimator
{
    private readonly string _sandboxMountBase = PathHelper.ToExtraMountAgentPath(ChatRunAccess.AttachmentDirectoryLabel);

    /// <summary>
    /// Estimates the manifest text that will be appended after attachments are staged for a real run.
    /// </summary>
    public string Build(IReadOnlyList<PendingChatAttachmentSummary> attachments)
    {
        if (attachments.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("User attachments:");
        for (int i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];
            var fileName = NormalizeFileName(attachment.FileName, i);
            var mediaType = string.IsNullOrWhiteSpace(attachment.MediaType)
                ? "application/octet-stream"
                : attachment.MediaType;
            var sizeBytes = Math.Max(0, attachment.SizeBytes);
            var filePath = $"{_sandboxMountBase}/{fileName}";

            builder.AppendLine($"- {fileName}")
                .AppendLine($"  media type: {mediaType}")
                .AppendLine($"  size: {sizeBytes} bytes")
                .AppendLine($"  file path: {filePath}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string NormalizeFileName(string fileName, int index)
    {
        var normalized = Path.GetFileName(fileName.Replace('\\', '/'));
        var sanitized = normalized.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? $"pending-attachment-{index + 1}"
            : sanitized;
    }
}
