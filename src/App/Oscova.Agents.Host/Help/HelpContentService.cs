using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Oscova.Agents.Host.Services.UI;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Host.Help;

public sealed record HelpContent(string TopicKey, string Title, string Html, string Icon,
    string? IconImageSource = null);

public interface IHelpContentService
{
    bool TopicExists(string topicKey);
    HelpContent? Load(string topicKey);
}

// Reads help markdown from the app_data/help folder, the single runtime location shipped with the app.
public sealed class HelpContentService(PluginPresentationService presentation,
    IAgentPluginCatalog plugins, ILogger<HelpContentService> logger) : IHelpContentService
{
    private static readonly string HelpContentRoot = PathUtil.HelpDocsPath;

    // Shared baseline plus a help-only rewrite of in-document .md links to ?helptopic= URLs.
    private static readonly MarkdownPipeline HostPipeline = BuildPipeline(HelpTopicRegistry.ResolveKeyByFile);

    private static MarkdownPipeline BuildPipeline(Func<string, string?> resolveLink)
    {
        var builder = new MarkdownPipelineBuilder();
        MarkdownRenderer.ApplyBaseline(builder);
        builder.DocumentProcessed += document => RewriteTopicLinks(document, resolveLink);
        builder.DocumentProcessed += RemoveLeadingTitle;
        return builder.Build();
    }

    public bool TopicExists(string topicKey)
    {
        if (topicKey.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
        {
            return Load(topicKey) is not null;
        }

        var descriptor = HelpTopicRegistry.Resolve(topicKey);
        return descriptor is not null && File.Exists(Path.Combine(HelpContentRoot, descriptor.RelativePath));
    }

    public HelpContent? Load(string topicKey)
    {
        if (topicKey.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
        {
            var found = presentation.FindHelp(topicKey);
            if (found is null) { return null; }

            var (plugin, channel, help) = found.Value;
            try
            {
                var markdown = presentation.ReadHelp(plugin, help);
                var html = MarkdownRenderer.ToHtml(markdown,
                    BuildPipeline(filename => presentation.ResolveSibling(plugin, filename)));
                return new HelpContent(topicKey, ExtractTitle(markdown) ?? topicKey, html,
                    channel is null ? "lucide:terminal" : "lucide:radio-tower", channel is null ? null : presentation.Icon(channel.Id));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or DecoderFallbackException)
            {
                logger.LogWarning(ex, "Plugin {PluginId} help {TopicId} could not be loaded.",
                    plugin.Manifest.Id, help.Id);
                plugins.ReportDiagnostic(new(AgentPluginLoadDiagnosticLevel.Warning, plugin.PluginDirectory,
                    $"Help '{help.Id}' could not be loaded: {ex.Message}"));
                return null;
            }
        }

        var descriptor = HelpTopicRegistry.Resolve(topicKey);
        if (descriptor is null) { return null; }

        var fullPath = Path.Combine(HelpContentRoot, descriptor.RelativePath);
        if (!File.Exists(fullPath)) { return null; }

        var hostMarkdown = File.ReadAllText(fullPath);
        // The title is the file's first heading; the rendered body has that heading stripped.
        var title = ExtractTitle(hostMarkdown) ?? topicKey;
        var hostHtml = MarkdownRenderer.ToHtml(hostMarkdown, HostPipeline);
        return new HelpContent(topicKey, title, hostHtml, descriptor.Icon);
    }

    // Reads the plain text of the first level-1 heading, which the panel shows as its title.
    // Returns null when the file has no leading H1 (the format requires one).
    private static string? ExtractTitle(string markdown)
    {
        foreach (var block in Markdown.Parse(markdown))
        {
            if (block is not HeadingBlock { Level: 1, Inline: { } inline })
            {
                continue;
            }

            var builder = new StringBuilder();
            foreach (var node in inline.Descendants())
            {
                if (node is LiteralInline literal)
                {
                    builder.Append(literal.Content.ToString());
                }
                else if (node is CodeInline code)
                {
                    builder.Append(code.Content);
                }
            }

            var title = builder.ToString().Trim();
            return title.Length == 0 ? null : title;
        }

        return null;
    }

    // Turns links to other help docs ([x](other.md)) into ?helptopic=<key> URLs so the help
    // panel swaps content in place instead of navigating the app. In-document help links are
    // bare flat filenames, so anything with a slash or scheme is left for ProcessLinks.
    private static void RewriteTopicLinks(MarkdownDocument document, Func<string, string?> resolveLink)
    {
        foreach (var node in document.Descendants())
        {
            if (node is not LinkInline link || string.IsNullOrEmpty(link.Url)) continue;

            var url = link.Url;
            if (url.Contains('/') || url.Contains(':')) continue;
            if (!url.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;

            var topicKey = resolveLink(url);
            // Known topic → its key. Unknown .md → the bare filename: the panel's loader rejects
            // the unregistered key, making the click an inert no-op. (A blank href would instead
            // resolve to the current URL and a click would close the panel.)
            link.Url = $"?helptopic={Uri.EscapeDataString(topicKey ?? url)}";
        }
    }

    // The panel header title is extracted from the markdown's leading H1, so rendering that same
    // H1 in the body would show the title twice. Drop the first block when it is a level-1
    // heading. The raw .md keeps its title for standalone and AI-agent reading.
    private static void RemoveLeadingTitle(MarkdownDocument document)
    {
        if (document.Count > 0 && document[0] is HeadingBlock { Level: 1 })
        {
            document.RemoveAt(0);
        }
    }
}
