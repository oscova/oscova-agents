using Microsoft.Extensions.AI;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.Library;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Utils;
using Oscova.Agents.Vector;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Library;

public sealed partial class LibraryIndexingService
{
    /// <inheritdoc />
    public async Task<SemanticSearchResult<LibrarySearchResult>> SearchAsync(
        string userId,
        string query,
        int maxResults,
        bool requireLocalEndpoint,
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("A Library search query is required.", nameof(query));
        }

        AllowedDirectory grantedLibrary = GetGrantedLibrary(userId, allowedDirectories);
        // An explicit search also lets the existing worker retry files paused by an earlier budget period.
        _requests.Writer.TryWrite(0);
        DurableVectorSpace? space = GetCurrentFingerprint();
        if (space is null)
        {
            return new([]);
        }

        DurableVectorIndexSnapshot load =
            await _index.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!IsFingerprintCurrent(load, space))
        {
            return new([]);
        }

        if (!File.Exists(_vectorDatabasePath))
        {
            return new([]);
        }

        var resolution = await _embeddingGeneratorFactory.CreateAsync(requireLocalEndpoint, cancellationToken).ConfigureAwait(false);
        using var generator = resolution.Generator;
        if (generator is null) { return new([], resolution.UnavailableReason); }

        await WriteEmbeddingEgressAsync(
            EgressAuditKinds.SourceEmbeddingSearch,
            cancellationToken).ConfigureAwait(false);
        Embedding<float> queryEmbedding = await generator.GenerateAsync(
            query,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var results = new List<LibrarySearchResult>();
        IReadOnlyList<DurableVectorHit> search = await _index.SearchAsync(
            space,
            queryEmbedding,
            50,
            cancellationToken).ConfigureAwait(false);
        foreach (DurableVectorHit result in search)
        {
            string relativePath = load.Sources.TryGetValue(
                result.SourceId,
                out DurableVectorSourceSnapshot? source)
                ? source.Metadata ?? result.SourceId
                : result.SourceId;
            if (IsSourceInvalidated(relativePath))
            {
                continue;
            }

            results.Add(new LibrarySearchResult(
                PathHelper.ResolveToAgentPath(
                    Path.Combine(
                        grantedLibrary.Value,
                        PathHelper.ToPlatformRelativePath(relativePath)),
                    allowedDirectories),
                result.Content,
                DecodePageNumber(result.Metadata)));
            if (results.Count == Math.Clamp(maxResults, 1, 50))
            {
                break;
            }
        }

        return new(results);
    }

    private static int? DecodePageNumber(string? metadata) =>
        int.TryParse(
            metadata,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out int pageNumber)
            ? pageNumber
            : null;

    /// <inheritdoc />
    public async Task<LibraryIndexStatus> GetIndexStatusAsync(
        string userId,
        bool requireLocalEndpoint,
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        CancellationToken cancellationToken = default)
    {
        _ = GetGrantedLibrary(userId, allowedDirectories);
        return await GetIndexStatusAsync(
            requireLocalEndpoint,
            cancellationToken).ConfigureAwait(false);
    }

    private AllowedDirectory GetGrantedLibrary(
        string userId,
        IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        if (!string.Equals(userId, _userId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Library workspace access is required.");
        }

        string libraryRoot = _userDirectoryService.GetWorkspaceDirectoryPath(
            userId,
            UserFileNames.LibraryWorkspaceDirectory);
        return allowedDirectories.FirstOrDefault(directory =>
            string.Equals(
                directory.Label,
                UserFileNames.LibraryWorkspaceDirectory,
                StringComparison.OrdinalIgnoreCase) &&
            directory.Kind == DirectorySourceKind.LocalPath &&
            PathHelper.ArePathsEqual(directory.Value, libraryRoot))
            ?? throw new UnauthorizedAccessException("Library workspace access is required.");
    }
}
