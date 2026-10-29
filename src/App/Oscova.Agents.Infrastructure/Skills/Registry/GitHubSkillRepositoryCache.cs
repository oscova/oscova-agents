using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Skills.Registry;
using Oscova.Agents.Utils;
using Oscova.Agents.Infrastructure.Downloads;

namespace Oscova.Agents.Infrastructure.Skills.Registry;

/// <summary>Maintains shared local repository copies and successful skill catalogs.</summary>
public sealed class GitHubSkillRepositoryCache(
    string cacheRoot,
    TimeProvider timeProvider,
    ILogger<GitHubSkillRepositoryCache> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(1);

    /// <summary>Returns a fresh repository snapshot or the last successful snapshot with a warning.</summary>
    public async Task<Snapshot> GetAsync(
        SkillRegistrySource source,
        string? token,
        bool airGapped,
        CancellationToken cancellationToken,
        Action<SkillRegistryPreparationProgress>? onProgress = null,
        bool refreshIfStale = true)
    {
        if (!Reference.IsValidName($"refs/heads/{source.Branch}"))
        {
            throw new GitHubRegistryException("branch must name a valid Git branch.");
        }

        var directory = GetCacheDirectory(source, token);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            onProgress?.Invoke(new(SkillRegistryPreparationStage.CheckingCache));
            // Fresh reads use immutable revision objects while acquisition stays serialized.
            var cached = await Task.Run(() => ReadCachedSnapshot(directory), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (cached.Snapshot is not null && (cached.Fresh || !refreshIfStale))
            {
                onProgress?.Invoke(new(SkillRegistryPreparationStage.Cached));
                return cached.Snapshot!;
            }

            onProgress?.Invoke(new(SkillRegistryPreparationStage.Waiting));
            // ponytail: one gate serializes acquisition, use per-source gates if preparation contention grows.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Native cancellation is handled before its exception leaves the synchronous worker.
                Snapshot? snapshot = await Task.Run(() =>
                {
                    try
                    {
                        return Prepare(source, token, directory, airGapped, cancellationToken, onProgress, refreshIfStale);
                    }
                    catch (UserCancelledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }
                }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return snapshot!;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or LibGit2SharpException)
        {
            throw new GitHubRegistryException($"Could not prepare {source.DisplayName} on branch {source.Branch}.", ex);
        }
    }

    /// <summary>Resolves an existing clone for reading pinned files without refreshing it.</summary>
    public string GetRepositoryPath(SkillRegistrySource source, string? token)
    {
        if (!PathHelper.TryResolveContainedPath(GetCacheDirectory(source, token), "repository", out var path))
        {
            throw new GitHubRegistryException("The cached repository contains a linked storage path.");
        }

        return path;
    }

    /// <summary>Partitions repository and API metadata by source and credential without storing the token.</summary>
    internal string GetCacheDirectory(SkillRegistrySource source, string? token)
    {
        var identity = JsonSerializer.Serialize(new[]
        {
            source.Owner.ToLowerInvariant(), source.Repo.ToLowerInvariant(), source.Branch, token ?? string.Empty
        });
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        if (!PathHelper.TryResolveContainedPath(cacheRoot, key, out var directory))
        {
            throw new GitHubRegistryException("The repository cache path is not contained in the cache directory.");
        }

        return directory;
    }

    /// <summary>Resolves the contained repository and catalog paths through their shared owner.</summary>
    private static (string Repository, string Catalog, string TemporaryCatalog) ResolveCachePaths(string directory)
    {
        if (!PathHelper.TryResolveContainedPath(directory, "repository", out var repositoryPath) ||
            !PathHelper.TryResolveContainedPath(directory, "catalog.json", out var catalogPath) ||
            !PathHelper.TryResolveContainedPath(directory, "catalog.json.tmp", out var temporaryCatalogPath))
        {
            throw new GitHubRegistryException("The cached repository contains a linked storage path.");
        }

        return (repositoryPath, catalogPath, temporaryCatalogPath);
    }

    /// <summary>Reads successful catalog metadata and validates its immutable revision.</summary>
    private (Snapshot? Snapshot, bool Fresh) ReadCachedSnapshot(string directory)
    {
        var paths = ResolveCachePaths(directory);
        var state = File.Exists(paths.Catalog)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(paths.Catalog)) : null;
        if (state is null || !HasRevision(paths.Repository, state.CommitSha)) { return (null, false); }

        var snapshot = new Snapshot(paths.Repository, state.CommitSha, state.SkillPaths, null);
        return (snapshot, timeProvider.GetUtcNow() - state.RefreshedAt < Freshness);
    }

    private Snapshot Prepare(
        SkillRegistrySource source, string? token, string directory, bool airGapped,
        CancellationToken cancellationToken,
        Action<SkillRegistryPreparationProgress>? onProgress,
        bool refreshIfStale)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var (repositoryPath, catalogPath, temporaryCatalogPath) = ResolveCachePaths(directory);
        // Another caller may have prepared this source while we waited for the gate.
        var cached = ReadCachedSnapshot(directory);
        if (cached.Snapshot is not null && (cached.Fresh || !refreshIfStale))
        {
            onProgress?.Invoke(new(SkillRegistryPreparationStage.Cached));
            return cached.Snapshot!;
        }

        var notifications = new ProgressNotificationThrottle();
        SkillRegistryPreparationProgress? lastReported = null;
        long receivedBytes = 0;
        void Report(SkillRegistryPreparationProgress progress)
        {
            receivedBytes = progress.ReceivedBytes;
            if (progress == lastReported) { return; }

            var force = progress.Stage != lastReported?.Stage ||
                        (progress.TotalItems > 0 && progress.CompletedItems == progress.TotalItems);
            if (!notifications.TryNotify(force)) { return; }

            lastReported = progress;
            onProgress?.Invoke(progress);
        }

        try
        {
            if (airGapped)
            {
                throw new GitHubRegistryException("Offline mode is on: downloading skill repositories is blocked.");
            }

            var options = CreateFetchOptions(token, cancellationToken, Report);
            var exists = Repository.IsValid(repositoryPath);
            if (!exists)
            {
                Repository.Init(repositoryPath);
            }

            using var repository = new Repository(repositoryPath);
            var refSpec = $"+refs/heads/{source.Branch}:refs/remotes/origin/{source.Branch}";
            if (!exists)
            {
                var url = $"https://github.com/{Uri.EscapeDataString(source.Owner)}/{Uri.EscapeDataString(source.Repo)}.git";
                repository.Network.Remotes.Add("origin", url, refSpec);
            }
            // Fetch only the selected branch, unrelated remote references can exceed Windows path limits.
            repository.Config.Set("core.longpaths", true);
            Report(new(SkillRegistryPreparationStage.Connecting));
            Commands.Fetch(repository, "origin", [refSpec], options, null);
            cancellationToken.ThrowIfCancellationRequested();
            var revision = repository.Branches[$"refs/remotes/origin/{source.Branch}"]?.Tip;
            if (revision is null)
            {
                throw new GitHubRegistryException($"Branch {source.Branch} has no available revision.");
            }

            // This checkout is owned by the cache, installed user skills are copied elsewhere.
            Report(new(SkillRegistryPreparationStage.PreparingFiles, receivedBytes));
            Commands.Checkout(repository, revision, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force,
                OnCheckoutProgress = (_, completed, total) =>
                    Report(new(SkillRegistryPreparationStage.PreparingFiles, receivedBytes, completed, total))
            });
            cancellationToken.ThrowIfCancellationRequested();
            Report(new(SkillRegistryPreparationStage.Scanning, receivedBytes));
            var paths = DiscoverSkillPaths(repositoryPath, repositoryPath, revision.Tree, cancellationToken).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var state = new State(revision.Sha, timeProvider.GetUtcNow(), paths);
            File.WriteAllText(temporaryCatalogPath, JsonSerializer.Serialize(state));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryCatalogPath, catalogPath, overwrite: true);
            return new Snapshot(repositoryPath, state.CommitSha, state.SkillPaths, null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is GitHubRegistryException or LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not refresh skill source {Source} on {Branch}.", source.DisplayName, source.Branch);
            if (cached.Snapshot is null)
            {
                throw new GitHubRegistryException($"Could not prepare {source.DisplayName} on branch {source.Branch}.", ex);
            }
            // The saved revision remains readable even if updating the working checkout failed.
            return cached.Snapshot with
            {
                Warning = $"{source.DisplayName}: Refresh failed. Using the last successfully cached skills."
            };
        }
    }

    private static bool HasRevision(string repositoryPath, string revision)
    {
        if (!Repository.IsValid(repositoryPath)) { return false; }

        using var repository = new Repository(repositoryPath);
        return repository.Lookup<Commit>(revision) is not null;
    }

    private static FetchOptions CreateFetchOptions(string? token,
        CancellationToken cancellationToken,
        Action<SkillRegistryPreparationProgress> onProgress)
    {
        return new FetchOptions
        {
            OnProgress = _ => !cancellationToken.IsCancellationRequested,
            OnTransferProgress = progress =>
            {
                var indexing = progress.TotalObjects > 0 && progress.ReceivedObjects >= progress.TotalObjects;
                onProgress(new(
                    indexing ? SkillRegistryPreparationStage.Indexing : SkillRegistryPreparationStage.Downloading,
                    progress.ReceivedBytes,
                    indexing ? progress.IndexedObjects : progress.ReceivedObjects,
                    progress.TotalObjects));
                return !cancellationToken.IsCancellationRequested;
            },
            CredentialsProvider = string.IsNullOrWhiteSpace(token) ? null : (url, _, _) =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var remote) ||
                    remote.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(remote.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GitHubRegistryException("GitHub authentication requires an HTTPS github.com endpoint.");
                }

                return new UsernamePasswordCredentials { Username = "x-access-token", Password = token };
            }
        };
    }

    private static IEnumerable<string> DiscoverSkillPaths(
        string root, string directory, Tree tree, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetFileName(file), "SKILL.md", StringComparison.OrdinalIgnoreCase)) { continue; }

            var relative = PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(root, file));
            if (!relative.Contains('/') || !PathHelper.TryResolveContainedPath(root, relative, out _)) { continue; }

            var entry = tree[relative];
            if (entry?.Target is Blob && entry.Mode is not Mode.SymbolicLink)
            {
                yield return relative[..^"/SKILL.md".Length];
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(child), ".git", StringComparison.OrdinalIgnoreCase)) { continue; }

            var relative = PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(root, child));
            if (!PathHelper.TryResolveContainedPath(root, relative, out _)) { continue; }

            foreach (var skillPath in DiscoverSkillPaths(root, child, tree, cancellationToken))
            {
                yield return skillPath;
            }
        }
    }

    /// <summary>Releases the shared cache gate when the host stops.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>Identifies one successfully cached revision and its local catalog.</summary>
    public sealed record Snapshot(string RepositoryPath, string CommitSha, IReadOnlyList<string> SkillPaths, string? Warning);

    private sealed record State(string CommitSha, DateTimeOffset RefreshedAt, IReadOnlyList<string> SkillPaths);
}
