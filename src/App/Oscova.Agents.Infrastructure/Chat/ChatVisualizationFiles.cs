using Oscova.Agents.Core.Interfaces;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Places visualization files beside the existing durable or ephemeral thread outputs.</summary>
public static class ChatVisualizationFiles
{
    public const string DirectoryLabel = "visualizations for this thread";

    /// <summary>Uses the attachment service's validated user and thread roots for visualization storage.</summary>
    public static string ResolveVisualizationFolderPath(this IChatAttachmentService attachments,
        string userId, string threadId, bool incognito)
    {
        var output = attachments.ResolveToolOutputFolderPath(userId, threadId, incognito);
        return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(output))!, "visualizations", Path.GetFileName(output));
    }
}
