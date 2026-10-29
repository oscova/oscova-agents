using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.ApiContracts.Skills.Registry;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Skills.Registry;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Skills;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Skills.Registry;

/// <summary>Serves skill discovery and selected files using the operator's acquisition choice.</summary>
public sealed class GitHubSkillRegistryService(
    GitHubSkillRepositoryCache cache,
    GitHubSkillApiCache apiCache,
    IHttpClientFactory clients,
    SkillRegistrySettings settings,
    ISystemCredentialService systemCredentialService,
    RunContextAccessor runContext,
    ILogger<GitHubSkillRegistryService> logger) : IGitHubSkillRegistryService
{
    public async Task<SkillRegistryCatalogResponse> EnumerateSkillsAsync(
        SkillRegistrySource source, CancellationToken cancellationToken,
        Action<SkillRegistryPreparationProgress>? onProgress = null,
        SkillRegistrySearchMethod? searchMethod = null)
    {
        IReadOnlyList<string> paths;
        string? warning;
        if ((searchMethod ?? settings.SearchMethod) == SkillRegistrySearchMethod.GitHubApi)
        {
            var snapshot = await GetApiSnapshotAsync(source, cancellationToken, onProgress).ConfigureAwait(false);
            paths = snapshot.Catalog.Files
                .Where(file => file.IsRegularFile && file.Path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Path[..file.Path.LastIndexOf('/')])
                .Distinct(StringComparer.Ordinal).ToArray();
            warning = snapshot.Warning;
        }
        else
        {
            var snapshot = await GetSnapshotAsync(source, cancellationToken, onProgress).ConfigureAwait(false);
            paths = snapshot.SkillPaths;
            warning = snapshot.Warning;
        }

        var catalog = new SkillRegistryCatalogResponse();
        foreach (var path in paths)
        {
            if (!string.IsNullOrEmpty(source.SkillRoot) &&
                PathHelper.GetRelativePathUnderRoot(path, source.SkillRoot, StringComparison.Ordinal) is null) { continue; }

            catalog.Entries.Add(new RemoteSkillCatalogEntry
            {
                Source = source,
                SkillName = path.Split('/')[^1],
                SkillPath = path
            });
        }

        if (warning is not null) { catalog.Errors.Add(warning); }

        return catalog;
    }

    public async Task<string?> FetchDescriptionAsync(RemoteSkillCatalogEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            if (settings.SearchMethod == SkillRegistrySearchMethod.GitHubApi)
            {
                var files = await ListSkillFilesAsync(entry, cancellationToken, SkillRegistrySearchMethod.GitHubApi).ConfigureAwait(false);
                var document = files.First(file =>
                    string.Equals(file.RepoPath, $"{entry.SkillPath}/SKILL.md", StringComparison.OrdinalIgnoreCase));
                using var contentStream = new MemoryStream();
                await DownloadFileAsync(document, contentStream, SkillImportLimits.MaxUncompressedZipSizeBytes, null, cancellationToken).ConfigureAwait(false);
                contentStream.Position = 0;
                using var reader = new StreamReader(contentStream);
                return ReadDescription(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            }

            var snapshot = await GetSnapshotAsync(entry.Source, cancellationToken, refreshIfStale: false).ConfigureAwait(false);
            using var repository = new Repository(snapshot.RepositoryPath);
            var tree = GetSkillTree(repository, snapshot, entry.SkillPath);
            var content = ((Blob)tree["SKILL.md"].Target).GetContentText();
            return ReadDescription(content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to read skill description for {Source}/{Path}.", entry.Source.DisplayName, entry.SkillPath);
            return null;
        }
    }

    public async Task<IReadOnlyList<RemoteSkillFile>> ListSkillFilesAsync(
        RemoteSkillCatalogEntry entry, CancellationToken cancellationToken, SkillRegistrySearchMethod? searchMethod = null)
    {
        if ((searchMethod ?? settings.SearchMethod) == SkillRegistrySearchMethod.GitHubApi)
        {
            var api = await GetApiSnapshotAsync(entry.Source, cancellationToken, refreshIfStale: false).ConfigureAwait(false);
            if (!api.Catalog.Files.Any(file => file.IsRegularFile &&
                string.Equals(file.Path, $"{entry.SkillPath}/SKILL.md", StringComparison.OrdinalIgnoreCase)))
            {
                throw new GitHubRegistryException($"No skill document is available at {entry.SkillPath}.");
            }

            return api.Catalog.Files
                .Where(file => file.IsRegularFile &&
                    PathHelper.GetRelativePathUnderRoot(file.Path, entry.SkillPath, StringComparison.Ordinal) is not null)
                .Select(file => new RemoteSkillFile(entry.Source, file.Path, file.Size, api.Catalog.TreeSha, file.Sha)).ToArray();
        }

        var snapshot = await GetSnapshotAsync(entry.Source, cancellationToken, refreshIfStale: false).ConfigureAwait(false);
        using var repository = new Repository(snapshot.RepositoryPath);
        var tree = GetSkillTree(repository, snapshot, entry.SkillPath);
        var files = new List<RemoteSkillFile>();
        CollectFiles(tree, entry.SkillPath, entry.Source, snapshot.CommitSha, files, cancellationToken);
        return files;
    }

    public async Task DownloadFileAsync(
        RemoteSkillFile file, Stream destination, long maxBytes, Action<long>? progress, CancellationToken cancellationToken)
    {
        var token = await ResolveTokenAsync(cancellationToken).ConfigureAwait(false);
        // The listed blob identity selects API reads even after the operator changes the method.
        if (file.BlobSha is not null)
        {
            if (file.Size > maxBytes) { throw DownloadLimit(maxBytes); }

            using var client = clients.CreateClient(GitHubSkillApiCache.ClientName);
            using var response = await GitHubSkillApiCache.SendAsync(client, file.Source,
                $"git/blobs/{Uri.EscapeDataString(file.BlobSha)}", token, true, cancellationToken).ConfigureAwait(false);
            using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await CopyBoundedAsync(content, destination, maxBytes, progress, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Listed files retain their revision while another caller refreshes the checkout.
        var repositoryPath = cache.GetRepositoryPath(file.Source, token);
        using var repository = new Repository(repositoryPath);
        var revision = repository.Lookup<Commit>(file.RevisionSha);
        var entry = revision?.Tree[file.RepoPath];
        if (entry?.Target is not Blob blob || entry.Mode is Mode.SymbolicLink)
        {
            throw new GitHubRegistryException($"The listed file {file.RepoPath} is unavailable at its cached revision.");
        }

        if (blob.Size > maxBytes)
        {
            throw DownloadLimit(maxBytes);
        }

        using var input = blob.GetContentStream();
        await CopyBoundedAsync(input, destination, maxBytes, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Uses one streaming byte limit for API blobs and cached repository files.</summary>
    private static async Task CopyBoundedAsync(
        Stream input, Stream destination, long maxBytes, Action<long>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            copied += read;
            if (copied > maxBytes)
            {
                throw DownloadLimit(maxBytes);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Invoke(read);
        }
    }

    private static ArgumentException DownloadLimit(long maxBytes) =>
        new($"The remote skill exceeds the {ByteSize.Format(maxBytes)} remaining download size limit.");

    private static string? ReadDescription(string content)
    {
        if (!SkillFrontmatter.TryRead(content, out var values)) { return null; }

        return values.TryGetValue("description", out var description) && !string.IsNullOrWhiteSpace(description)
            ? description : null;
    }

    private async Task<GitHubSkillApiCache.Snapshot> GetApiSnapshotAsync(
        SkillRegistrySource source, CancellationToken cancellationToken,
        Action<SkillRegistryPreparationProgress>? onProgress = null, bool refreshIfStale = true)
    {
        var token = await ResolveTokenAsync(cancellationToken).ConfigureAwait(false);
        return await apiCache.GetAsync(source, token, runContext.Current?.AirGapped == true,
            cancellationToken, onProgress, refreshIfStale).ConfigureAwait(false);
    }

    private async Task<GitHubSkillRepositoryCache.Snapshot> GetSnapshotAsync(
        SkillRegistrySource source, CancellationToken cancellationToken,
        Action<SkillRegistryPreparationProgress>? onProgress = null,
        bool refreshIfStale = true)
    {
        var token = await ResolveTokenAsync(cancellationToken).ConfigureAwait(false);
        return await cache.GetAsync(source, token, runContext.Current?.AirGapped == true,
            cancellationToken, onProgress, refreshIfStale).ConfigureAwait(false);
    }

    private Task<string?> ResolveTokenAsync(CancellationToken cancellationToken) =>
        systemCredentialService.ResolveFieldAsync(GitHubSkillsCredential.Key, GitHubSkillsCredential.TokenFieldName, cancellationToken);

    private static Tree GetSkillTree(Repository repository, GitHubSkillRepositoryCache.Snapshot snapshot, string skillPath)
    {
        var tree = repository.Lookup<Commit>(snapshot.CommitSha)?.Tree[skillPath]?.Target as Tree;
        if (tree?["SKILL.md"]?.Target is not Blob || tree["SKILL.md"].Mode is Mode.SymbolicLink)
        {
            throw new GitHubRegistryException($"No skill document is available at {skillPath}.");
        }

        return tree;
    }

    private static void CollectFiles(
        Tree tree, string prefix, SkillRegistrySource source, string revision,
        List<RemoteSkillFile> files, CancellationToken cancellationToken)
    {
        foreach (var entry in tree)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"{prefix}/{entry.Name}";
            if (entry.Target is Tree child)
            {
                CollectFiles(child, path, source, revision, files, cancellationToken);
            }
            else if (entry.Target is Blob blob && entry.Mode is not Mode.SymbolicLink)
            {
                files.Add(new RemoteSkillFile(source, path, blob.Size, revision));
            }
        }
    }
}
