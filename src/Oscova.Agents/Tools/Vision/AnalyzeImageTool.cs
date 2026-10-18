using Microsoft.Extensions.AI;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tools.Vision;

/// <summary>
/// Exposes a single-image vision call as an AI tool. Constructed per-run with the same
/// <see cref="IChatClient"/> the chat agent is using, so the tool always routes image
/// analysis through the currently selected (vision-capable) provider.
/// </summary>
public sealed class AnalyzeImageTool(
    IChatClient chatClient,
    Func<IReadOnlyList<AllowedDirectory>> workspaceResolver)
{
    /// <summary>
    /// Gets the stable function name used when exposing the image analysis tool.
    /// </summary>
    public const string ToolName = "analyze_image";

    private const long MaxImageBytes = 10L * 1024 * 1024;
    private readonly IWorkspaceFileAccess _files = new WorkspaceFileAccess(workspaceResolver);

    /// <summary>
    /// Creates the AI function wrapper for image analysis.
    /// </summary>
    /// <returns>An AI function that validates a local image path and asks the configured chat client to analyze it.</returns>
    public AIFunction AsAITool() =>
        AIFunctionFactory.Create(
            AnalyzeAsync,
            new AIFunctionFactoryOptions
            {
                Name = ToolName,
                Description = """
                    Analyzes one image on disk and returns a text answer.
                    path must identify an existing file within Workspace Access.
                    Supported extensions: .png, .jpg, .jpeg, .gif, .webp.
                    Maximum image size: 10485760 bytes (10 MiB).
                    """
            });

    [Description("Analyzes one image on disk and returns a text answer.")]
    private async Task<string> AnalyzeAsync(
        [Description("File path: relative to your working directory, or an absolute path listed under Workspace Access")]
        [WorkspacePath(Access = WorkspacePathAccess.Read)] string path,
        [Description("The question to answer about the image.")] string question,
        CancellationToken ct)
    {
        // Physical reads enforce the grants even when the function is invoked without middleware.
        var entry = _files.GetEntry(path);
        if (entry is null || (entry.Attributes & FileAttributes.Directory) != 0)
        {
            return $"Image not found at '{path}'.";
        }

        var mediaType = MediaTypeFromExtension(Path.GetExtension(entry.Path));
        if (mediaType is null)
        {
            return $"Unsupported image type for '{path}'.";
        }

        byte[] bytes;
        using (var stream = _files.OpenRead(path))
        {
            var size = stream.Length;
            if (size > MaxImageBytes)
            {
                return $"Image '{path}' exceeds the {MaxImageBytes / (1024 * 1024)} MB size limit.";
            }

            bytes = new byte[(int)size];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        }
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(question),
            new DataContent(bytes, mediaType)
        ]);

        var response = await chatClient.GetResponseAsync([message], cancellationToken: ct).ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }

    private static string? MediaTypeFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null
    };
}
