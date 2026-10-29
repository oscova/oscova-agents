using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Turns agent and host markdown into styled terminal output.
/// </summary>
public static class CliMarkdown
{
    /// <summary>
    /// Renders markdown into a renderable; unsupported constructs fall through as plain text.
    /// </summary>
    public static IRenderable Render(string markdown)
    {
        var document = Markdown.Parse(markdown ?? string.Empty);
        var blocks = new List<IRenderable>();
        foreach (var block in document)
        {
            RenderBlock(block, blocks);
        }

        return blocks.Count == 0 ? new Text(string.Empty) : new Rows(blocks);
    }

    private static void RenderBlock(Block block, List<IRenderable> output)
    {
        switch (block)
        {
            case HeadingBlock heading:
                output.Add(RenderHeading(heading));
                break;
            case ParagraphBlock paragraph:
                output.Add(RenderInlines(paragraph.Inline, CliTheme.Value));
                break;
            case FencedCodeBlock fenced:
                output.Add(RenderCode(fenced.Lines.ToString(), fenced.Info));
                break;
            case CodeBlock code:
                output.Add(RenderCode(code.Lines.ToString(), null));
                break;
            case ListBlock list:
                output.Add(RenderList(list));
                break;
            case QuoteBlock quote:
                output.Add(RenderQuote(quote));
                break;
            case ThematicBreakBlock:
                var rule = new Rule();
                rule.Style = CliTheme.Border;
                output.Add(rule);
                break;
            case LeafBlock leaf:
                output.Add(new Text(leaf.Lines.ToString(), CliTheme.Value));
                break;
            case ContainerBlock container:
                foreach (var child in container)
                {
                    RenderBlock(child, output);
                }

                break;
        }
    }

    private static IRenderable RenderHeading(HeadingBlock heading)
    {
        var text = RenderInlines(heading.Inline, CliTheme.Heading);
        if (heading.Level > 2)
        {
            return text;
        }

        // A rule needs its title as markup, so the heading text is flattened and escaped.
        var rule = new Rule(Flatten(heading.Inline).EscapeMarkup());
        rule.Style = CliTheme.Border;
        rule.Justification = Justify.Left;
        return rule;
    }

    private static IRenderable RenderCode(string code, string? language)
    {
        var panel = new Panel(new Text(code.TrimEnd(), CliTheme.Value))
            .Border(BoxBorder.Rounded)
            .BorderStyle(CliTheme.Border);

        if (!string.IsNullOrWhiteSpace(language))
        {
            panel.Header = new PanelHeader($"[{CliTheme.LabelMarkup}]{language.EscapeMarkup()}[/]");
        }

        return panel;
    }

    private static IRenderable RenderList(ListBlock list)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(1))
            .AddColumn();

        var number = ParseOrderedStart(list);
        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            var marker = list.IsOrdered ? $"{number++}." : "-";
            var content = new List<IRenderable>();
            foreach (var itemBlock in item)
            {
                RenderBlock(itemBlock, content);
            }

            grid.AddRow(new Text(marker, CliTheme.Label), new Rows(content));
        }

        return grid;
    }

    private static IRenderable RenderQuote(QuoteBlock quote)
    {
        var content = new List<IRenderable>();
        foreach (var child in quote)
        {
            RenderBlock(child, content);
        }

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(1))
            .AddColumn();
        grid.AddRow(new Text("|", CliTheme.Label), new Rows(content));
        return grid;
    }

    private static int ParseOrderedStart(ListBlock list) =>
        int.TryParse(list.OrderedStart, out var start) ? start : 1;

    private static Paragraph RenderInlines(ContainerInline? container, Style baseStyle)
    {
        var paragraph = new Paragraph();
        AppendInlines(paragraph, container, baseStyle);
        return paragraph;
    }

    private static void AppendInlines(Paragraph paragraph, ContainerInline? container, Style style)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    paragraph.Append(literal.Content.ToString(), style);
                    break;
                case EmphasisInline emphasis:
                    AppendInlines(paragraph, emphasis, style.Combine(EmphasisStyle(emphasis)));
                    break;
                case CodeInline code:
                    paragraph.Append(code.Content, new Style(CliTheme.Accent));
                    break;
                case LinkInline link:
                    AppendInlines(paragraph, link, style);
                    if (!string.IsNullOrWhiteSpace(link.Url))
                    {
                        paragraph.Append($" ({link.Url})", CliTheme.Label);
                    }

                    break;
                case LineBreakInline lineBreak:
                    paragraph.Append(lineBreak.IsHard ? Environment.NewLine : " ", style);
                    break;
                case ContainerInline nested:
                    AppendInlines(paragraph, nested, style);
                    break;
                default:
                    paragraph.Append(inline.ToString() ?? string.Empty, style);
                    break;
            }
        }
    }

    private static Style EmphasisStyle(EmphasisInline emphasis) =>
        emphasis.DelimiterCount >= 2 ? new Style(decoration: Decoration.Bold) : new Style(decoration: Decoration.Italic);

    private static string Flatten(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case ContainerInline nested:
                    builder.Append(Flatten(nested));
                    break;
            }
        }

        return builder.ToString();
    }
}
