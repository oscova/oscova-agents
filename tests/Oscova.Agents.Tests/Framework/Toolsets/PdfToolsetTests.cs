using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Oscova.Agents.Tests.Framework.Toolsets;

/// <summary>
/// Exercises <see cref="PdfToolset"/> against small PDFs generated on the fly via PdfPig's
/// <see cref="PdfDocumentBuilder"/>. Keeps fixtures out of source control and tests self-contained.
/// </summary>
public sealed class PdfToolsetTests : IDisposable
{
    private readonly string _workspaceDirectory;
    private readonly PdfToolset _toolset;

    public PdfToolsetTests()
    {
        _workspaceDirectory = Path.Combine(
            Environment.CurrentDirectory,
            "pdf-toolset-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceDirectory);
        _toolset = new PdfToolset(() =>
        [
            new(_workspaceDirectory, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceDirectory))
        {
            Directory.Delete(_workspaceDirectory, recursive: true);
        }
    }

    [Fact]
    public void ExtractPdfText_WithNoRange_ReturnsAllPages()
    {
        var pdfPath = CreatePdf("multi.pdf",
            "Alpha paragraph with enough real content to be considered meaningful text.",
            "Beta paragraph with enough real content to be considered meaningful text.");

        var output = _toolset.ExtractPdfText(pdfPath);

        Assert.Contains("--- Page 1 ---", output);
        Assert.Contains("--- Page 2 ---", output);
        Assert.Contains("Alpha", output);
        Assert.Contains("Beta", output);
    }

    [Fact]
    public void ExtractPdfText_WithSinglePageRange_ReturnsThatPageOnly()
    {
        var pdfPath = CreatePdf("single.pdf",
            "Alpha paragraph with enough real content to be considered meaningful text.",
            "Beta paragraph with enough real content to be considered meaningful text.");

        var output = _toolset.ExtractPdfText(pdfPath, "1");

        Assert.Contains("Alpha", output);
        Assert.DoesNotContain("Beta", output);
    }

    [Fact]
    public void ExtractPdfText_WithRange_ReturnsOnlyPagesInRange()
    {
        var pdfPath = CreatePdf("range.pdf",
            "Alpha paragraph with enough real content to be considered meaningful text.",
            "Beta paragraph with enough real content to be considered meaningful text.",
            "Gamma paragraph with enough real content to be considered meaningful text.");

        var output = _toolset.ExtractPdfText(pdfPath, "2-3");

        Assert.DoesNotContain("Alpha", output);
        Assert.Contains("Beta", output);
        Assert.Contains("Gamma", output);
    }

    [Fact]
    public void ExtractPdfText_WithInvalidRange_Throws()
    {
        var pdfPath = CreatePdf("invalid-range.pdf", "Alpha text.");

        Assert.Throws<ToolFailureException>(() => _toolset.ExtractPdfText(pdfPath, "abc"));
    }

    [Fact]
    public void ExtractPdfText_NonexistentPath_Throws()
    {
        var missing = Path.Combine(_workspaceDirectory, "does-not-exist.pdf");

        var exception = Assert.Throws<ToolFailureException>(() => _toolset.ExtractPdfText(missing));
        Assert.DoesNotContain(missing, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractPdfText_NonPdfExtension_Throws()
    {
        var textPath = Path.Combine(_workspaceDirectory, "notes.txt");
        File.WriteAllText(textPath, "hello");

        Assert.Throws<ToolFailureException>(() => _toolset.ExtractPdfText(textPath));
    }

    [Fact]
    public void ExtractPdfText_WhenPdfHasNoText_ThrowsScannedImageGuidance()
    {
        // A PDF with a single page and no text content should trigger the low-signal fallback.
        var pdfPath = Path.Combine(_workspaceDirectory, "blank.pdf");
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        File.WriteAllBytes(pdfPath, builder.Build());

        var exception = Assert.Throws<ToolFailureException>(() => _toolset.ExtractPdfText(pdfPath));

        Assert.Equal("The PDF contains no extractable text.", exception.Message);
        Assert.Equal("Use an OCR-capable tool for scanned pages.", exception.Fix);
    }

    [Fact]
    public void ExtractPdfText_SavesFullTextPastBudget()
    {
        const int pageCount = 12;
        var pageText = new string('A', 2500);

        var pdfPath = Path.Combine(_workspaceDirectory, "large.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(pageText, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        }

        File.WriteAllBytes(pdfPath, builder.Build());

        var store = new RecordingToolOutputStore();
        var toolset = new PdfToolset(() =>
        [
            new(_workspaceDirectory, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ], outputStore: store);
        var output = toolset.ExtractPdfText(pdfPath);

        Assert.Contains("Showing the first 16000", output);
        Assert.Contains("Full output saved", output);
        Assert.Contains("--- Page 12 ---", store.Saved);
        Assert.DoesNotContain("--- Page 12 ---", output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SearchPdfText_ReportsTotalAndSavesAllSnippets(bool aliasInput)
    {
        var pdfPath = CreatePdf("search.pdf",
            "Alpha paragraph with enough real content to be considered meaningful text.",
            "First NEEDLE result with enough surrounding text for a useful discovery snippet.",
            "Second NEEDLE result with enough surrounding text for a useful discovery snippet.");

        var store = new RecordingToolOutputStore();
        var toolset = new PdfToolset(() =>
        [
            new(_workspaceDirectory, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/default")
        ], outputStore: store);
        string result = toolset.SearchPdfText(aliasInput ? "/workspace/default/search.pdf" : pdfPath, "needle", limit: 1);

        Assert.Contains("PDF: /workspace/default/search.pdf |", result);
        Assert.Contains("Pages: 3 | Matching pages: 2", result);
        Assert.Contains("--- Page 2 ---", result);
        Assert.DoesNotContain("--- Page 3 ---", result);
        Assert.Contains("Showing 1 of 2 results. Full results saved", result);
        Assert.Contains("--- Page 3 ---", store.Saved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SearchPdfText_WithNoMatchStillReportsPageCount(bool aliasInput)
    {
        var pdfPath = CreatePdf("no-match.pdf",
            "Alpha paragraph with enough real content to be considered meaningful text.",
            "Beta paragraph with enough real content to be considered meaningful text.");

        var toolset = new PdfToolset(() =>
        [
            new(_workspaceDirectory, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/default")
        ]);
        var output = toolset.SearchPdfText(aliasInput ? "/workspace/default/no-match.pdf" : pdfPath, "missing phrase");

        Assert.Contains("PDF: /workspace/default/no-match.pdf |", output);
        Assert.Contains("Pages: 2", output);
        Assert.Contains("No matching pages", output);
    }

    [Fact]
    public void SearchPdfText_WhenShortPdfContainsQuery_ReturnsMatch()
    {
        var pdfPath = CreatePdf("short-match.pdf", "APPROVED");

        var output = _toolset.SearchPdfText(pdfPath, "APPROVED");

        Assert.Contains("--- Page 1 ---", output);
        Assert.Contains("APPROVED", output);
        Assert.DoesNotContain("likely scanned images", output);
    }

    [Fact]
    public void PasswordProtectedPdf_ThrowsEncryptedFileFailures()
    {
        var pdfPath = CreatePasswordProtectedPdf();

        Assert.Equal(
            "The PDF is encrypted and cannot be read.",
            Assert.Throws<ToolFailureException>(() => _toolset.ExtractPdfText(pdfPath)).Message);
        Assert.Equal(
            "The PDF is encrypted and cannot be searched.",
            Assert.Throws<ToolFailureException>(() => _toolset.SearchPdfText(pdfPath, "anything")).Message);
    }

    private string CreatePdf(string fileName, params string[] pageTexts)
    {
        var pdfPath = Path.Combine(_workspaceDirectory, fileName);
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var pageText in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(pageText, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        }

        File.WriteAllBytes(pdfPath, builder.Build());
        return pdfPath;
    }

    private string CreatePasswordProtectedPdf()
    {
        const string passwordProtectedPdf = """
            JVBERi0xLjMKJeLjz9MKMSAwIG9iago8PAovUHJvZHVjZXIgPDMxMDYwNDJhMzg+Cj4+CmVuZG9iagoyIDAgb2JqCjw8
            Ci9UeXBlIC9QYWdlcwovQ291bnQgMQovS2lkcyBbIDQgMCBSIF0KPj4KZW5kb2JqCjMgMCBvYmoKPDwKL1R5cGUgL0Nh
            dGFsb2cKL1BhZ2VzIDIgMCBSCj4+CmVuZG9iago0IDAgb2JqCjw8Ci9UeXBlIC9QYWdlCi9SZXNvdXJjZXMgPDwKPj4K
            L01lZGlhQm94IFsgMC4wIDAuMCA3MiA3MiBdCi9QYXJlbnQgMiAwIFIKPj4KZW5kb2JqCjUgMCBvYmoKPDwKL1YgMQov
            UiAyCi9MZW5ndGggNDAKL1AgNDI5NDk2NzI5MgovRmlsdGVyIC9TdGFuZGFyZAovTyA8ODQyZTk2OTZlY2RmMTNhODI5
            ZjNiNjNjNWI5NjE0Y2M2MjU0YjdlMDM4NWIyNDdiYWI5MGE1MDgxNzljMDM0MD4KL1UgPDAyZGEwOTgyYmQ3MGFiZGE4
            YmY4MWQ3MDY3NGVhMjhjNDdhMTM5N2NkYmUzYzI5ZWJiZWZjNzZhNDJjZGRkNDY+Cj4+CmVuZG9iagp4cmVmCjAgNgow
            MDAwMDAwMDAwIDY1NTM1IGYgCjAwMDAwMDAwMTUgMDAwMDAgbiAKMDAwMDAwMDA1OSAwMDAwMCBuIAowMDAwMDAwMTE4
            IDAwMDAwIG4gCjAwMDAwMDAxNjcgMDAwMDAgbiAKMDAwMDAwMDI1OSAwMDAwMCBuIAp0cmFpbGVyCjw8Ci9TaXplIDYK
            L1Jvb3QgMyAwIFIKL0luZm8gMSAwIFIKL0lEIFsgPDY0NjY2MzYxNjYzNTM0MzIzNzM5MzAzMzMyMzAzNjY0NjQzMTMx
            MzQzNDMxNjQzMDYyMzU2MjYxMzkzNjMxNjI+IDw2NDY2NjM2MTY2MzUzNDMyMzczOTMwMzMzMjMwMzY2NDY0MzEzMTM0
            MzQzMTY0MzA2MjM1NjI2MTM5MzYzMTYyPiBdCi9FbmNyeXB0IDUgMCBSCj4+CnN0YXJ0eHJlZgo0NzMKJSVFT0YK
            """;
        var pdfPath = Path.Combine(_workspaceDirectory, "password-protected.pdf");
        File.WriteAllBytes(pdfPath, Convert.FromBase64String(passwordProtectedPdf));
        return pdfPath;
    }
}
