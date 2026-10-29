using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Oscova.Agents.Conversations;
using Oscova.Agents.Host.Artifacts;
using Oscova.Agents.Host.Constants;

namespace Oscova.Agents.Host.Services.UI;

// Single Markdig configuration shared by chat messages and help content.
// Raw HTML is disabled and unsafe link schemes are stripped because the rendered
// output is injected via MarkupString, so untrusted input must not reach the DOM.
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = ApplyBaseline(new MarkdownPipelineBuilder()).Build();

    // Chat replies keep single line breaks so poems, addresses and short lists render as written.
    private static readonly MarkdownPipeline ChatPipeline =
        ApplyBaseline(new MarkdownPipelineBuilder()).UseSoftlineBreakAsHardlineBreak().Build();

    // Applies the shared baseline: raw HTML disabled, unsafe link schemes stripped,
    // external links forced to open in a new tab. Callers that need extra link
    // handling (e.g. the help panel) build their own pipeline on top of this.
    public static MarkdownPipelineBuilder ApplyBaseline(MarkdownPipelineBuilder builder)
    {
        builder.UseAutoLinks();
        builder.UseDefinitionLists();
        builder.UseEmphasisExtras();
        builder.UseGridTables();
        builder.UsePipeTables();
        builder.UseTaskLists();
        // DisableHtml escapes raw HTML blocks/inline HTML instead of passing them through.
        builder.DisableHtml();
        builder.DocumentProcessed += ProcessTables;
        builder.DocumentProcessed += ProcessLinks;
        return builder;
    }

    public static string ToHtml(string markdown, MarkdownPipeline? pipeline = null) =>
        string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, pipeline ?? Pipeline);

    /// <summary>Routes assistant file links and images through the owning chat's authorized file endpoints.</summary>
    public static string ToChatHtml(string markdown, ChatThread? thread, string? agentId,
        IReadOnlyList<ThreadArtifact> artifacts, bool isRunActive, int? tableIndex = null)
    {
        var document = Markdown.Parse(markdown, ChatPipeline);
        foreach (var link in document.Descendants<LinkInline>().ToArray())
        {
            var artifact = thread is null ? null
                : ThreadArtifactProjector.ResolveReference(link.Url, agentId, artifacts, thread.ProjectId is not null);
            if (thread is null || artifact is null)
            {
                if (link.IsImage) { RenderImageAsLink(link); }

                continue;
            }

            var downloadUrl = "/" + ApiRoutes.Chat.ArtifactDownload(thread.Id, artifact.Id);
            if (link.IsImage)
            {
                // Completion changes the URL after the server saves a newly streamed file reference.
                link.Url = "/" + ApiRoutes.Chat.ArtifactContent(thread.Id, artifact.Id) + "?streaming=" + isRunActive;
                link.GetAttributes().AddPropertyIfNotExist("loading", "lazy");
                if (link.Parent is LinkInline) { continue; }
                // A native anchor keeps image previews keyboard-accessible and modified clicks usable.
                var previewLink = new LinkInline(downloadUrl, string.Empty);
                previewLink.GetAttributes().AddPropertyIfNotExist("data-chat-artifact", artifact.Id);
                link.ReplaceBy(previewLink, false);
                previewLink.AppendChild(link);
            }
            else
            {
                link.Url = downloadUrl;
                link.GetAttributes().AddPropertyIfNotExist("data-chat-artifact", artifact.Id);
            }
        }

        if (tableIndex is null) { return Markdown.ToHtml(document, ChatPipeline); }

        // Expanded tables use the same sanitized tree and authorized links as the reply.
        var table = document.Descendants<Table>().ElementAtOrDefault(tableIndex.Value);
        if (table is null) { return string.Empty; }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        ChatPipeline.Setup(renderer);
        renderer.Render(table);
        return writer.ToString();
    }

    // A browser image fetch bypasses the Egress Audit, which records only server calls,
    // so images that are not thread artifacts render as plain links.
    private static void RenderImageAsLink(LinkInline image)
    {
        image.IsImage = false;
        if (image.FirstChild is null) { image.AppendChild(new LiteralInline(image.Url ?? string.Empty)); }
    }

    private static void ProcessTables(MarkdownDocument document)
    {
        foreach (var table in document.Descendants().OfType<Table>())
        {
            var attributes = table.GetAttributes();
            attributes.AddClass("table");
            attributes.AddClass("table-sm");
        }
    }

    // Blanks unsafe URLs and routes external links to a new browser tab.
    private static void ProcessLinks(MarkdownDocument document)
    {
        foreach (var node in document.Descendants())
        {
            string? url = node switch
            {
                LinkInline link => link.Url,
                AutolinkInline autolink => autolink.Url,
                _ => null
            };
            if (url is null) continue;

            if (IsUnsafeUrl(url))
            {
                if (node is LinkInline unsafeLink) unsafeLink.Url = string.Empty;
                else if (node is AutolinkInline unsafeAutolink) unsafeAutolink.Url = string.Empty;
            }
            else if (IsExternalUrl(url))
            {
                var attributes = node.GetAttributes();
                attributes.AddPropertyIfNotExist("target", "_blank");
                attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
            }
        }
    }

    private static bool IsUnsafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        // Browsers ignore whitespace and control characters inside a scheme, so strip them before checking.
        var stripped = url.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray();
        var normalized = new string(stripped).ToLowerInvariant();
        return normalized.StartsWith("javascript:", StringComparison.Ordinal)
            || normalized.StartsWith("data:", StringComparison.Ordinal)
            || normalized.StartsWith("vbscript:", StringComparison.Ordinal);
    }

    private static bool IsExternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
