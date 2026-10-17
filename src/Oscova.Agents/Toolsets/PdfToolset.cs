using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using Oscova.Agents.Harness;
using Oscova.Agents.Workspace;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Exposes bounded PDF text discovery and page-range extraction tools.
/// </summary>
public sealed class PdfToolset : IAgentToolset
{
    // Below this many non-whitespace characters the PDF is assumed to be scanned images / unreadable.
    private const int MinMeaningfulChars = 20;

    private const int MaxSearchResults = 20;

    private const int SearchSnippetContextChars = 180;

    private readonly IWorkspaceFileAccess _files;
    private readonly IToolOutputStore? _outputStore;
    private readonly Func<IReadOnlyList<AllowedDirectory>> _directories;

    /// <summary>
    /// Creates PDF tools that resolve agent-facing paths against the active run's allowed directories.
    /// </summary>
    public PdfToolset(
        Func<IReadOnlyList<AllowedDirectory>>? workspaceResolver = null,
        IWorkspaceFileAccess? fileAccess = null,
        IToolOutputStore? outputStore = null)
    {
        Func<IReadOnlyList<AllowedDirectory>> resolver = workspaceResolver ?? (() => []);
        _files = fileAccess ?? new WorkspaceFileAccess(resolver);
        _outputStore = outputStore;
        _directories = resolver;
    }

    /// <summary>
    /// Extracts text from a PDF file, optionally limited to an inclusive page range.
    /// </summary>
    /// <param name="path">The relative, agent-facing, or host PDF path the active workspace allows reading.</param>
    /// <param name="pageRange">An optional single page or range such as <c>5</c> or <c>2-7</c>.</param>
    /// <returns>Extracted page text, a scanned/encrypted PDF message, or a saved-output notice.</returns>
    [DisplayName("extract_text")]
    [AgentTool]
    [Description("Extracts text from a PDF file. Long output shows the start and saves the full text to a file you can read or search.")]
    public string ExtractPdfText(
        [Description("File path: relative to your working directory, or an absolute path within a listed granted folder")]
        [WorkspacePath(Access = WorkspacePathAccess.Read)]
        string path,
        [Description("Optional page range. Examples: '5' for a single page, '2-7' for a range. Omit or leave empty for all pages.")]
        string? pageRange = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ToolFailureException("A PDF path is required.");
        }

        using Stream stream = OpenPdfStream(path);
        using var document = OpenPdf(stream);
        if (document?.IsEncrypted != false)
        {
            throw new ToolFailureException("The PDF is encrypted and cannot be read.");
        }

        var pageCount = document.NumberOfPages;
        ParseRange(pageRange, pageCount, out var startPage, out var endPage);

        var builder = new StringBuilder();
        // Count non-whitespace characters from pageText only so our scanned-image heuristic
        // is not skewed by the "--- Page N ---" headers we inject.
        var meaningfulChars = 0;

        for (var pageNumber = startPage; pageNumber <= endPage; pageNumber++)
        {
            var pageText = GetPageText(document, pageNumber);

            foreach (var character in pageText)
            {
                if (!char.IsWhiteSpace(character))
                {
                    meaningfulChars++;
                }
            }

            builder.Append("--- Page ").Append(pageNumber).AppendLine(" ---")
                .AppendLine(pageText)
                .AppendLine();
        }

        var combined = builder.ToString();

        // If essentially no extractable text came back, assume the PDF is scanned images.
        if (meaningfulChars < MinMeaningfulChars)
        {
            throw new ToolFailureException(
                "The PDF contains no extractable text.",
                "Use an OCR-capable tool for scanned pages.");
        }

        return ToolOutput.LimitText(_outputStore, "pdf", combined);
    }

    /// <summary>
    /// Searches extractable PDF text and returns bounded, page-numbered snippets.
    /// </summary>
    /// <param name="path">The relative, agent-facing, or host PDF path the active workspace allows reading.</param>
    /// <param name="query">The literal text to find, matched without case sensitivity.</param>
    /// <param name="limit">The maximum number of matching pages to return.</param>
    /// <returns>PDF page metadata and a bounded list of matching snippets.</returns>
    [DisplayName("search_text")]
    [AgentTool]
    [Description("Searches a PDF for literal text and returns total pages plus bounded, page-numbered snippets. Use this before extracting a large PDF.")]
    public string SearchPdfText(
        [Description("File path: relative to your working directory, or an absolute path within a listed granted folder")]
        [WorkspacePath(Access = WorkspacePathAccess.Read)]
        string path,
        [Description("Literal text to find, matched without case sensitivity")] string query,
        [Description("Maximum number of matching pages to return, up to 20")] int limit = MaxSearchResults)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ToolFailureException("A PDF path is required.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ToolFailureException("A PDF search query is required.");
        }

        limit = Math.Clamp(limit, 1, MaxSearchResults);

        using Stream stream = OpenPdfStream(path);
        using var document = OpenPdf(stream);
        if (document?.IsEncrypted != false)
        {
            throw new ToolFailureException("The PDF is encrypted and cannot be searched.");
        }

        var pageCount = document.NumberOfPages;
        if (pageCount < 1)
        {
            throw new ToolFailureException("The PDF has no pages.");
        }

        var matches = new ToolOutputResults(limit);
        var meaningfulChars = 0;
        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            var pageText = GetPageText(document, pageNumber);
            meaningfulChars += pageText.Count(static character => !char.IsWhiteSpace(character));

            var matchIndex = pageText.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (matchIndex >= 0)
            {
                matches.Record($"--- Page {pageNumber} ---\n{BuildSearchSnippet(pageText, matchIndex, query.Length)}");
            }
        }

        if (matches.Total == 0 && meaningfulChars < MinMeaningfulChars)
        {
            throw new ToolFailureException(
                "The PDF contains no extractable text.",
                "Use an OCR-capable tool for scanned pages.");
        }

        var directories = _directories();
        string hostPath = PathHelper.ResolveAuthorizedHostPath(path, directories, WorkspacePathAccess.Read);
        path = PathHelper.ResolveToAgentPath(hostPath, directories);
        if (matches.Total == 0)
        {
            return $"PDF: {path} | Pages: {pageCount} | No matching pages.";
        }

        return $"PDF: {path} | Pages: {pageCount} | Matching pages: {matches.Total}\n"
            + matches.Compose(_outputStore, "pdf-search");
    }

    private Stream OpenPdfStream(string path)
    {
        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolFailureException("The requested file is not a PDF.", "Choose a file ending in .pdf.");
        }

        try
        {
            return _files.OpenRead(path);
        }
        catch (FileNotFoundException)
        {
            throw new ToolFailureException("The requested PDF was not found.", "Check the path and try again.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new ToolFailureException(
                "The PDF path could not be accessed.",
                "Check the path and workspace access, then try again.");
        }

    }

    private static PdfDocument? OpenPdf(Stream stream)
    {
        // Normalize parser failures so malformed bytes do not leak dependency exception details to the agent.
        try
        {
            return PdfDocument.Open(stream);
        }
        catch (PdfDocumentEncryptedException)
        {
            return null;
        }
        catch (Exception)
        {
            throw new ToolFailureException("The PDF could not be opened.");
        }
    }

    private static string GetPageText(PdfDocument document, int pageNumber)
    {
        // ContentOrderTextExtractor preserves reading order and handles spacing, unlike page.Text.
        return ContentOrderTextExtractor.GetText(document.GetPage(pageNumber)) ?? string.Empty;
    }

    private static string BuildSearchSnippet(string pageText, int matchIndex, int queryLength)
    {
        var start = Math.Max(0, matchIndex - SearchSnippetContextChars);
        var end = Math.Min(pageText.Length, matchIndex + queryLength + SearchSnippetContextChars);
        var source = pageText.AsSpan(start, end - start);
        var builder = new StringBuilder(source.Length);
        var previousWasWhitespace = false;

        foreach (var character in source)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        var snippet = builder.ToString().Trim();
        if (start > 0)
        {
            snippet = "..." + snippet;
        }

        if (end < pageText.Length)
        {
            snippet += "...";
        }

        return snippet;
    }

    /// <summary>
    /// Parses the optional <paramref name="range"/> string into inclusive 1-indexed start/end page numbers.
    /// Accepts <c>null</c>/empty (all pages), a single integer, or <c>"start-end"</c>.
    /// </summary>
    private static void ParseRange(string? range, int pageCount, out int startPage, out int endPage)
    {
        if (pageCount < 1)
        {
            throw new ToolFailureException("The PDF has no pages.");
        }

        if (string.IsNullOrWhiteSpace(range))
        {
            startPage = 1;
            endPage = pageCount;
            return;
        }

        var trimmed = range.Trim();
        var dashIndex = trimmed.IndexOf('-');

        if (dashIndex < 0)
        {
            if (!int.TryParse(trimmed, out var singlePage))
            {
                throw new ToolFailureException("The PDF page range is invalid.", "Use a page number such as '5' or a range such as '2-7'.");
            }

            startPage = ClampPage(singlePage, pageCount);
            endPage = startPage;
            return;
        }

        var startText = trimmed.Substring(0, dashIndex);
        var endText = trimmed.Substring(dashIndex + 1);

        if (!int.TryParse(startText, out var start) || !int.TryParse(endText, out var end))
        {
            throw new ToolFailureException("The PDF page range is invalid.", "Use a page number such as '5' or a range such as '2-7'.");
        }

        startPage = ClampPage(start, pageCount);
        endPage = ClampPage(end, pageCount);

        if (startPage > endPage)
        {
            throw new ToolFailureException("The PDF page range starts after it ends.", "Use an ascending page range.");
        }
    }

    private static int ClampPage(int pageNumber, int pageCount)
    {
        if (pageNumber < 1)
        {
            return 1;
        }

        if (pageNumber > pageCount)
        {
            return pageCount;
        }

        return pageNumber;
    }
}
