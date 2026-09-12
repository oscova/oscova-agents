using Oscova.Agents.Persistence;
using Oscova.Agents.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Core;

namespace Oscova.Agents.Definitions;

/// <summary>Stores per-user personal context for shared agents beneath a caller-selected root.</summary>
public sealed class SharedAgentPersonalContextRepository(
    Func<string, string> personalContextRootResolver,
    long maxTextSizeBytes,
    YamlConverter yamlCodec) : ISharedAgentPersonalContextRepository
{
    private const string ContextFileName = "context.md";
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private readonly Func<string, string> _personalContextRootResolver = personalContextRootResolver ??
        throw new ArgumentNullException(nameof(personalContextRootResolver));
    private readonly long _maxTextSizeBytes = maxTextSizeBytes > 0
        ? maxTextSizeBytes
        : throw new ArgumentOutOfRangeException(nameof(maxTextSizeBytes));

    /// <summary>Returns the saved personal-context body, or an empty string when no file exists.</summary>
    public async Task<string> GetAsync(string userId, AgentDefinition agent, CancellationToken ct = default)
    {
        var root = ResolveRoot(userId);
        var slugs = ListSharedFolders(root);
        var existing = await TryFindByIdAsync(root, agent.Id, slugs).ConfigureAwait(false);
        if (existing is null)
        {
            return string.Empty;
        }

        var fileText = await ReadFileTextAsync(existing.Value.Path).ConfigureAwait(false);
        var (_, body) = MarkdownFrontmatter.Split(fileText);
        return body;
    }

    /// <summary>Saves personal context; cancellation is honored before mutation admission, then the save completes.</summary>
    public async Task SaveAsync(string userId, AgentDefinition agent, string body, CancellationToken ct = default)
    {
        var root = ResolveRoot(userId);
        await SaveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Reuse the existing folder if we've saved for this agent before; never rename it.
            var slugs = ListSharedFolders(root);
            var existing = await TryFindByIdAsync(root, agent.Id, slugs).ConfigureAwait(false);
            var targetPath = existing?.Path ?? ResolveContextPath(root, ResolveNewSlug(agent, slugs));

            var yaml = yamlCodec.Serialize(new SharedAgentPersonalContextFile { AgentId = agent.Id });
            var content = MarkdownFrontmatter.Combine(yaml, body ?? string.Empty);
            if (Encoding.UTF8.GetByteCount(content) > _maxTextSizeBytes)
            {
                throw new InvalidOperationException($"Content exceeds the maximum size of {_maxTextSizeBytes} bytes.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, content).ConfigureAwait(false);
        }
        finally
        {
            _ = SaveGate.Release();
        }
    }

    /// <summary>Deletes matching files; cancellation is honored before each mutation, then that delete completes.</summary>
    public async Task<int> DeleteForUsersAsync(IEnumerable<string> userIds, AgentDefinition agent, CancellationToken ct = default)
    {
        var deletedCount = 0;
        foreach (var userId in userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var root = ResolveRoot(userId);
            var slugs = ListSharedFolders(root);
            var existing = await TryFindByIdAsync(root, agent.Id, slugs).ConfigureAwait(false);
            if (existing is null)
            {
                continue;
            }

            if (!File.Exists(existing.Value.Path))
            {
                throw new FileNotFoundException("File not found.");
            }

            File.Delete(existing.Value.Path);
            deletedCount++;
        }

        return deletedCount;
    }

    private async Task<(string Slug, string Path)?> TryFindByIdAsync(
        string root,
        string agentId,
        IReadOnlyList<string> slugs)
    {
        foreach (var slug in slugs)
        {
            var path = ResolveContextPath(root, slug);
            if (!File.Exists(path))
            {
                continue;
            }

            var fileText = await ReadFileTextAsync(path).ConfigureAwait(false);
            try
            {
                var (yaml, _) = MarkdownFrontmatter.Split(fileText);
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    continue;
                }

                var meta = yamlCodec.Deserialize<SharedAgentPersonalContextFile>(yaml);
                if (string.Equals(meta?.AgentId, agentId, StringComparison.Ordinal))
                {
                    return (slug, path);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or YamlException)
            {
                // Hand-edited or unrelated file at <slug>/context.md; skip.
            }
        }

        return null;
    }

    private static string ResolveNewSlug(AgentDefinition agent, IReadOnlyList<string> existingSlugs)
    {
        var taken = new HashSet<string>(existingSlugs, StringComparer.OrdinalIgnoreCase);
        return Slugs.MakeUnique(Slugs.ForAgentFolder(agent.Name), taken);
    }

    private static List<string> ListSharedFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(root)
            .Select(static directory => new DirectoryInfo(directory).Name)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)];
    }

    private string ResolveRoot(string userId)
    {
        var root = _personalContextRootResolver(userId);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("A personal-context root is required.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static string ResolveContextPath(string root, string slug)
    {
        var path = Path.GetFullPath(Path.Combine(root, slug, ContextFileName));
        if (!PathHelper.IsPathWithinDirectory(path, root) || PathHelper.HasSymlinkInPath(path, root))
        {
            throw new UnauthorizedAccessException("The personal-context path is outside the selected root.");
        }

        return path;
    }

    private async Task<string> ReadFileTextAsync(string path)
    {
        if (new FileInfo(path).Length > _maxTextSizeBytes)
        {
            throw new InvalidOperationException($"File exceeds the maximum size of {_maxTextSizeBytes} bytes.");
        }

        return await File.ReadAllTextAsync(path).ConfigureAwait(false);
    }
}

/// <summary>
/// YAML frontmatter shape for a shared agent's per-user personal context file.
/// </summary>
internal sealed class SharedAgentPersonalContextFile
{
    public string AgentId { get; set; } = string.Empty;
}
