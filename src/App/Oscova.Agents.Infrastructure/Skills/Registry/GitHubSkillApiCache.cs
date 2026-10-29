using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Skills.Registry;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Skills.Registry;

/// <summary>Shares successful GitHub tree metadata without downloading repository contents.</summary>
public sealed class GitHubSkillApiCache(
    GitHubSkillRepositoryCache repositories,
    IHttpClientFactory clients,
    TimeProvider timeProvider,
    ILogger<GitHubSkillApiCache> logger) : IDisposable
{
    internal const string ClientName = "github-skills";
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Returns complete tree metadata, retaining a successful snapshot when refresh is unavailable.</summary>
    public async Task<Snapshot> GetAsync(
        SkillRegistrySource source, string? token, bool airGapped, CancellationToken cancellationToken,
        Action<SkillRegistryPreparationProgress>? onProgress = null, bool refreshIfStale = true)
    {
        if (!Reference.IsValidName($"refs/heads/{source.Branch}"))
        {
            throw new GitHubRegistryException("branch must name a valid Git branch.");
        }

        var directory = repositories.GetCacheDirectory(source, token);
        var path = ResolvePath(directory, "api-catalog.json");
        try
        {
            onProgress?.Invoke(new(SkillRegistryPreparationStage.CheckingCache));
            var cached = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (cached is not null && (IsFresh(cached) || !refreshIfStale))
            {
                onProgress?.Invoke(new(SkillRegistryPreparationStage.Cached));
                return new(cached, null);
            }

            onProgress?.Invoke(new(SkillRegistryPreparationStage.Waiting));
            // Sources prepare independently, while callers of the same source reuse one refresh.
            var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cached = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (cached is not null && (IsFresh(cached) || !refreshIfStale))
                {
                    onProgress?.Invoke(new(SkillRegistryPreparationStage.Cached));
                    return new(cached, null);
                }

                try
                {
                    if (airGapped) { throw new GitHubRegistryException("Offline mode is on. API discovery requires previously cached metadata."); }

                    onProgress?.Invoke(new(SkillRegistryPreparationStage.Connecting));
                    using var client = clients.CreateClient(ClientName);
                    using var response = await SendAsync(client, source,
                        $"git/trees/{Uri.EscapeDataString(source.Branch)}?recursive=1", token, false, cancellationToken).ConfigureAwait(false);
                    onProgress?.Invoke(new(SkillRegistryPreparationStage.ReadingMetadata));
                    var catalog = await response.Content.ReadFromJsonAsync<Catalog>(cancellationToken).ConfigureAwait(false)
                        ?? throw new GitHubRegistryException("GitHub returned no tree metadata.");
                    if (catalog.Truncated)
                    {
                        throw new GitHubRegistryException("The GitHub API tree is incomplete because this repository is too large. The operator can explicitly select Local repository downloads in Manage sources.");
                    }

                    if (string.IsNullOrEmpty(catalog.TreeSha))
                    {
                        throw new GitHubRegistryException("GitHub returned tree metadata without an immutable revision.");
                    }

                    foreach (var file in catalog.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Metadata names need lexical containment, filesystem links matter only when writing files.
                        var fullPath = Path.GetFullPath(Path.Combine(directory, PathHelper.ToPlatformRelativePath(file.Path)));
                        if (!PathHelper.IsPathWithinDirectory(fullPath, directory) ||
                            file.Size < 0 || string.IsNullOrEmpty(file.Sha))
                        {
                            throw new GitHubRegistryException("GitHub returned invalid file metadata.");
                        }
                    }

                    catalog.RefreshedAt = timeProvider.GetUtcNow();
                    Directory.CreateDirectory(directory);
                    var temporary = ResolvePath(directory, "api-catalog.json.tmp");
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(catalog), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, path, overwrite: true);
                    return new(catalog, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to refresh GitHub metadata for {Source} on {Branch}.", source.DisplayName, source.Branch);
                    if (cached is null) { throw; }

                    onProgress?.Invoke(new(SkillRegistryPreparationStage.Cached));
                    return new(cached, $"{source.DisplayName}@{source.Branch}: Using cached API metadata. {ex.Message}");
                }
            }
            finally { gate.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException)
        {
            throw new GitHubRegistryException($"Could not read GitHub metadata for {source.DisplayName} on branch {source.Branch}. {ex.Message}", ex);
        }
    }

    /// <summary>Requests selected raw blobs or tree metadata with the shared credential and guarded HTTP client.</summary>
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, SkillRegistrySource source, string relativePath, string? token, bool raw, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Uri.EscapeDataString(source.Owner)}/{Uri.EscapeDataString(source.Repo)}/{relativePath}");
        request.Headers.UserAgent.ParseAdd("Oscova-Agents/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(raw ? "application/vnd.github.raw+json" : "application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token)) { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HTTP timeouts are source failures, while caller cancellation must still stop preparation.
            throw new GitHubRegistryException("The GitHub request timed out. Try the source again after GitHub becomes available.", ex);
        }

        if (response.IsSuccessStatusCode) { return response; }

        var status = response.StatusCode;
        var rateLimited = status == HttpStatusCode.TooManyRequests ||
            (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"));
        response.Dispose();
        var message = rateLimited
            ? "GitHub API rate limit reached. Wait for the limit to reset, or ask the operator to configure a token under Admin > GitHub Skill Search."
            : status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "GitHub denied repository access. The operator must check the shared token and its Contents read permission.",
                HttpStatusCode.NotFound => "The GitHub repository, branch or selected file was not found. Check the configured source and token access.",
                _ => $"GitHub returned HTTP {(int)status}. Try the source again after GitHub becomes available."
            };
        throw new GitHubRegistryException(message);
    }

    private bool IsFresh(Catalog catalog) => timeProvider.GetUtcNow() - catalog.RefreshedAt < Freshness;

    private static string ResolvePath(string directory, string name)
    {
        if (!PathHelper.TryResolveContainedPath(directory, name, out var path))
        {
            throw new GitHubRegistryException("The API metadata cache contains a linked storage path.");
        }

        return path;
    }

    private static async Task<Catalog?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) { return null; }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Catalog>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values) { gate.Dispose(); }
    }

    /// <summary>Pairs complete metadata with any warning from an unavailable refresh.</summary>
    public sealed record Snapshot(Catalog Catalog, string? Warning);

    /// <summary>Persists only immutable Git object metadata and its successful refresh time.</summary>
    public sealed class Catalog
    {
        [JsonPropertyName("sha")] public string TreeSha { get; init; } = string.Empty;
        [JsonPropertyName("tree")] public IReadOnlyList<FileEntry> Files { get; init; } = [];
        [JsonPropertyName("truncated")] public bool Truncated { get; init; }
        public DateTimeOffset RefreshedAt { get; set; }
    }

    /// <summary>Identifies repository files without retaining their contents.</summary>
    public sealed class FileEntry
    {
        [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
        [JsonPropertyName("mode")] public string Mode { get; init; } = string.Empty;
        [JsonPropertyName("sha")] public string Sha { get; init; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonIgnore] public bool IsRegularFile => Type == "blob" && Mode is "100644" or "100755";
    }
}
