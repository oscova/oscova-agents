using Oscova.Agents.Conversations;
using Oscova.Agents.Utils;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Describes attachments using granted aliases without placing file bytes in chat history.</summary>
public sealed class ChatAttachmentManifestBuilder
{
    /// <summary>Lists file-tool addresses and identifies attachments owned by the server.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Callers receive the builder through dependency injection.")]
    public string Build(IReadOnlyList<ChatAttachment> attachments, bool deviceProject = false)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return string.Empty;
        }

        string mountBase = PathHelper.ToExtraMountAgentPath(ChatRunAccess.AttachmentDirectoryLabel);

        var builder = new StringBuilder();
        builder.AppendLine("User attachments:");
        if (deviceProject)
        {
            builder.AppendLine("These files remain on the server. Read them with the file tool environment=workspace. They are read-only and are not device-shell paths.");
        }

        foreach (var attachment in attachments)
        {
            var filePath = $"{mountBase}/{attachment.StoredFileName}";
            builder.AppendLine($"- {attachment.DisplayName}")
                .AppendLine($"  media type: {attachment.MediaType}")
                .AppendLine($"  size: {attachment.SizeBytes} bytes")
                .AppendLine($"  file path: {filePath}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Concatenates the user's message with a manifest when attachments exist.</summary>
    public string Compose(string userText, IReadOnlyList<ChatAttachment> attachments, bool deviceProject = false)
    {
        var manifest = Build(attachments, deviceProject);
        if (string.IsNullOrEmpty(manifest))
        {
            return userText;
        }

        if (string.IsNullOrEmpty(userText))
        {
            return manifest;
        }

        return userText + "\n\n" + manifest;
    }
}
