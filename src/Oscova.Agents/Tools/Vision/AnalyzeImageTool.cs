using Microsoft.Extensions.AI;
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
public sealed class AnalyzeImageTool(IChatClient chatClient)
{
    /// <summary>
    /// Gets the stable function name used when exposing the image analysis tool.
    /// </summary>
    public const string ToolName = "analyze_image";

    private const long MaxImageBytes = 10L * 1024 * 1024;

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
                Description = "Analyzes a single image at the given local file path and " +
                              "answers the provided question about it. Returns a text description."
            });

    [Description("Analyzes one image on disk and returns a text answer.")]
    private async Task<string> AnalyzeAsync(
        [Description("Absolute local path to the image file.")] string path,
        [Description("The question to answer about the image.")] string question,
        CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return $"Path must be absolute: '{path}'.";
        }

        if (!File.Exists(path))
        {
            return $"Image not found at '{path}'.";
        }

        var mediaType = MediaTypeFromExtension(Path.GetExtension(path));
        if (mediaType is null)
        {
            return $"Unsupported image type for '{path}'.";
        }

        var size = new FileInfo(path).Length;
        if (size > MaxImageBytes)
        {
            return $"Image '{path}' exceeds the {MaxImageBytes / (1024 * 1024)} MB size limit.";
        }

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
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
