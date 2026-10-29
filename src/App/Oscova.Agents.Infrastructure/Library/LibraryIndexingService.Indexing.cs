using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Text;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Utils;
using Oscova.Agents.Vector;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Oscova.Agents.Infrastructure.Library;

public sealed partial class LibraryIndexingService
{
    private const int EmbeddingBatchSize = 128;

    private async Task<BatchOutcome> ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A missing Library root is not evidence that every committed source was deleted.
        if (!Directory.Exists(GetLibraryRoot()))
        {
            return default;
        }

        DurableVectorIndexSnapshot load =
            await _index.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        // Deletions never require an embedding provider, so purge them before provider resolution.
        await PurgeMissingSourcesAsync(load, cancellationToken).ConfigureAwait(false);
        GeneratorResolution resolution = await TryCreateGeneratorAsync(
            _privacySettings.IsLocalOnly,
            cancellationToken).ConfigureAwait(false);
        using IEmbeddingGenerator<string, Embedding<float>>? generator = resolution.Generator;
        if (generator is null || resolution.Space is null)
        {
            return default;
        }

        // A provider identity change invalidates the entire embedding space.
        if (!IsFingerprintCurrent(load, resolution.Space))
        {
            await _index.ResetAsync(resolution.Space, cancellationToken).ConfigureAwait(false);
            load = await _index.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        int processed = 0;
        int failed = 0;
        // Files mutations only signal that something changed; this scan reconciles the final folder state.
        foreach (string sourcePath in EnumerateLibraryFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = GetRelativePath(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            bool supported = SupportedExtensions.Contains(extension);
            long length;
            string contentHash = string.Empty;
            try
            {
                length = new FileInfo(sourcePath).Length;
                if (supported && length <= UserFileLimits.MaxUploadSizeBytes)
                {
                    contentHash = await ComputeHashAsync(
                        sourcePath,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await MarkFailedAsync(
                    relativePath,
                    string.Empty,
                    resolution.Space,
                    ex,
                    cancellationToken).ConfigureAwait(false);
                processed++;
                failed++;
                continue;
            }

            string? skipReason = !supported
                ? UnsupportedReason
                : length > UserFileLimits.MaxUploadSizeBytes
                    ? OversizedReason
                    : null;
            if (skipReason is not null)
            {
                if (IsSameSkippedState(load, relativePath, skipReason))
                {
                    continue;
                }

                await MarkSkippedAsync(
                    relativePath,
                    string.Empty,
                    skipReason,
                    resolution.Space,
                    cancellationToken).ConfigureAwait(false);
                processed++;
                continue;
            }

            if (load.Sources.TryGetValue(GetSourceId(relativePath), out DurableVectorSourceSnapshot? existing) &&
                existing.State is DurableVectorSourceState.Committed or
                    DurableVectorSourceState.Skipped or DurableVectorSourceState.Failed &&
                string.Equals(existing.GenerationId, contentHash, StringComparison.Ordinal))
            {
                // Reconciliation scans the whole folder; unchanged terminal sources need no work.
                continue;
            }

            bool fileFailed = await IndexFileAsync(
                sourcePath,
                relativePath,
                extension,
                generator,
                resolution.Space,
                contentHash,
                cancellationToken).ConfigureAwait(false);
            processed++;
            if (fileFailed)
            {
                failed++;
            }
        }

        return new BatchOutcome(processed, failed, Completed: true);
    }

    private async Task<bool> IndexFileAsync(
        string sourcePath,
        string relativePath,
        string extension,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        DurableVectorSpace space,
        string contentHash,
        CancellationToken cancellationToken)
    {
        try
        {
            // Text extraction preserves PDF page numbers so search results can cite the original source.
            SourceReadResult source = await ReadSourceTextAsync(
                sourcePath,
                extension,
                cancellationToken).ConfigureAwait(false);
            if (source.SkipReason is not null)
            {
                await MarkSkippedAsync(
                    relativePath,
                    contentHash,
                    source.SkipReason,
                    space,
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            var chunks = new List<SourceChunk>();
            foreach (SourceText page in source.Pages)
            {
                chunks.AddRange(Chunk(page.Text).Select(
                    text => new SourceChunk(text, page.PageNumber)));
            }

            if (chunks.Count == 0)
            {
                await MarkSkippedAsync(
                    relativePath,
                    contentHash,
                    EmptyTextReason,
                    space,
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            var embeddings = new List<Embedding<float>>(chunks.Count);
            foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
            {
                await WriteEmbeddingEgressAsync(
                    EgressAuditKinds.SourceEmbeddingIndexing,
                    cancellationToken).ConfigureAwait(false);
                GeneratedEmbeddings<Embedding<float>> generated = await generator.GenerateAsync(
                    batch.Select(static chunk => chunk.Content),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                embeddings.AddRange(generated);
            }

            var records = new List<DurableVectorChunk>(chunks.Count);
            for (int index = 0; index < chunks.Count; index++)
            {
                records.Add(new DurableVectorChunk(
                    chunks[index].Content,
                    embeddings[index].Vector,
                    index,
                    EncodePageNumber(chunks[index].PageNumber)));
            }

            await _index.ReplaceAsync(
                space,
                GetSourceId(relativePath),
                contentHash,
                records,
                sourceMetadata: relativePath,
                cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(
                relativePath,
                contentHash,
                space,
                ex,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private static async Task<SourceReadResult> ReadSourceTextAsync(
        string sourcePath,
        string extension,
        CancellationToken cancellationToken)
    {
        if (!extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            string text = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text)
                ? new SourceReadResult([], EmptyTextReason)
                : new SourceReadResult([new SourceText(text, null)]);
        }

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(sourcePath);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new SourceReadResult([], EncryptedPdfReason);
        }

        using (document)
        {
            if (document.IsEncrypted)
            {
                return new SourceReadResult([], EncryptedPdfReason);
            }

            var pages = new List<SourceText>();
            for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = ContentOrderTextExtractor.GetText(document.GetPage(pageNumber))
                    ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    pages.Add(new SourceText(text, pageNumber));
                }
            }

            return pages.Count == 0
                ? new SourceReadResult([], ImageOnlyPdfReason)
                : new SourceReadResult(pages);
        }
    }

    private static IReadOnlyList<string> Chunk(string text)
    {
#pragma warning disable SKEXP0050 // The existing memory index uses this package-provided token-aware splitter.
        IReadOnlyList<string> lines =
            TextChunker.SplitPlainTextLines(text, 100, tokenCounter: AgentTokenCounter.CountTokens);
        return TextChunker.SplitPlainTextParagraphs(
            lines,
            400,
            80,
            tokenCounter: AgentTokenCounter.CountTokens);
#pragma warning restore SKEXP0050
    }

    private async Task MarkSkippedAsync(
        string relativePath,
        string contentHash,
        string reason,
        DurableVectorSpace space,
        CancellationToken cancellationToken)
    {
        await _index.SetSourceStateAsync(
            space,
            GetSourceId(relativePath),
            contentHash,
            DurableVectorSourceState.Skipped,
            reason,
            relativePath,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkFailedAsync(
        string relativePath,
        string contentHash,
        DurableVectorSpace space,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await _index.SetSourceStateAsync(
            space,
            GetSourceId(relativePath),
            contentHash,
            DurableVectorSourceState.Failed,
            "Indexing failed. Check the embedding provider and source file.",
            relativePath,
            cancellationToken).ConfigureAwait(false);

        using IDisposable? scope = BeginNotificationHandledScope();
        _logger.LogError(
            exception,
            "Library file indexing failed for user {UserId} and source {RelativePath}.",
            _userId,
            relativePath);
    }

    private async Task PurgeMissingSourcesAsync(
        DurableVectorIndexSnapshot manifest,
        CancellationToken cancellationToken)
    {
        foreach ((string sourceId, DurableVectorSourceSnapshot entry) in manifest.Sources)
        {
            string relativePath = entry.Metadata ?? sourceId;
            if (TryResolveSourcePath(relativePath, out string sourcePath) &&
                File.Exists(sourcePath))
            {
                continue;
            }

            await _index.DeleteSourceAsync(
                sourceId,
                manifest.VectorDimensions > 0 ? manifest.VectorDimensions : GetConfiguredDimensions(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryResolveSourcePath(
        string relativePath,
        out string sourcePath)
    {
        sourcePath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        try
        {
            string libraryRoot = GetLibraryRoot();
            sourcePath = Path.GetFullPath(
                Path.Combine(libraryRoot, PathHelper.ToPlatformRelativePath(relativePath)));
            // Manifest paths are untrusted persisted input; containment and symlinks are the trust boundary.
            return PathHelper.IsPathWithinDirectory(sourcePath, libraryRoot) &&
                !PathHelper.HasSymlinkInPath(sourcePath, libraryRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string GetRelativePath(string sourcePath) =>
        PathHelper.ToSlashSeparatedRelativePath(
            Path.GetRelativePath(GetLibraryRoot(), sourcePath));

    private string GetLibraryRoot() =>
        _userDirectoryService.GetWorkspaceDirectoryPath(
            _userId,
            UserFileNames.LibraryWorkspaceDirectory);

    private IEnumerable<string> EnumerateLibraryFiles()
    {
        string root = GetLibraryRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(
            root,
            "*",
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            });
    }

    private static async Task<string> ComputeHashAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool IsSameSkippedState(
        DurableVectorIndexSnapshot manifest,
        string relativePath,
        string reason) =>
        manifest.Sources.TryGetValue(GetSourceId(relativePath), out DurableVectorSourceSnapshot? entry) &&
        entry.State == DurableVectorSourceState.Skipped &&
        string.Equals(entry.Reason, reason, StringComparison.Ordinal);

    private static string? EncodePageNumber(int? pageNumber) =>
        pageNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct BatchOutcome(int ProcessedCount, int FailedCount, bool Completed);

    private sealed record SourceReadResult(
        IReadOnlyList<SourceText> Pages,
        string? SkipReason = null);

    private readonly record struct SourceText(string Text, int? PageNumber);

    private readonly record struct SourceChunk(string Content, int? PageNumber);
}
