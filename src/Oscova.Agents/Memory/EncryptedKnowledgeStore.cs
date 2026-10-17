using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Persistence;
using Oscova.Agents.Search;
using Oscova.Agents.Secrets;
using Oscova.Agents.Utils;
using YamlDotNet.Core;

namespace Oscova.Agents.Memory;

/// <summary>Shares encrypted concept persistence, discovery and derived indexes across caller-owned knowledge bundles.</summary>
public sealed class EncryptedKnowledgeStore
{
    private const string FileSuffix = ".md" + EncryptedFileContent.FileExtension;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationGates = new(StringComparer.Ordinal);
    private static readonly Regex MarkdownLinks = new(@"\[[^\]]*\]\((?:<(?<target>[^>]+)>|(?<target>[^)\s]+))(?:\s+""[^""]*"")?\)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly string _userId;
    private readonly string _userRoot;
    private readonly string _root;
    private readonly long _maxTextSizeBytes;
    private readonly IUserFileBackupService _fileBackups;
    private readonly IContentProtector _protector;
    private readonly YamlConverter _yaml = new();

    /// <summary>Binds a bundle to its user's existing encrypted files and backup policy.</summary>
    public EncryptedKnowledgeStore(string userId, string userRoot, string relativeRoot, long maxTextSizeBytes,
        IUserFileBackupService fileBackups, IContentProtector protector)
    {
        _userId = userId;
        _userRoot = Path.GetFullPath(userRoot);
        if (!PathHelper.TryResolveContainedPath(_userRoot, PathHelper.ToPlatformRelativePath(relativeRoot), out var root))
        {
            throw new UnauthorizedAccessException("Knowledge root is outside the user's directory or contains a linked path.");
        }
        _root = root;
        _maxTextSizeBytes = maxTextSizeBytes;
        _fileBackups = fileBackups;
        _protector = protector;
    }

    /// <summary>Returns the canonical ID for a new or existing contained concept.</summary>
    public string ResolveConceptId(string conceptId)
    {
        if (string.IsNullOrWhiteSpace(conceptId) || Path.IsPathRooted(conceptId) ||
            Uri.TryCreate(conceptId, UriKind.Absolute, out _) || conceptId.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentMemoryValidationException("conceptId must be a nonempty bundle-relative path without .md.");
        }
        var id = ResolveReference(conceptId);
        if (IsReserved(id))
        {
            throw new AgentMemoryValidationException("conceptId cannot name index or log at any directory level.");
        }
        return id;
    }

    /// <summary>Reads one concept or directory index, deriving absent indexes from actual saved documents.</summary>
    public async Task<string?> ReadAsync(string? document = null, CancellationToken ct = default)
    {
        var id = string.IsNullOrWhiteSpace(document) ? "index" : ResolveReference(document);
        var text = await ReadTextAsync(id, ct).ConfigureAwait(false);
        if (Path.GetFileName(id).Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            return text ?? await BuildIndexAsync(ParentDirectory(id), new Dictionary<string, string>(), ct).ConfigureAwait(false);
        }
        if (text is null) { return null; }
        if (IsReserved(id)) { return text; }
        var concept = ParseConcept(id, text);
        var links = GetLinks(concept).ToArray();
        var result = $"conceptId: {id}\ndocument: {LinkPath(id)}\n\n{text}";
        if (links.Length > 0)
        {
            result += "\n\nLinked documents:\n" + string.Join("\n", links.Select(link =>
                $"- {LinkPath(link)}" + (File.Exists(ResolveFile(link)) ? string.Empty : " (not saved)")));
        }
        return result;
    }

    /// <summary>Searches saved metadata and bodies without embedding calls or a separate index database.</summary>
    public async Task<string> FindAsync(string query, string? excludedDirectory = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || LexicalSearchStrategy.FindTokenMatches(query, query).Count == 0)
        {
            throw new AgentMemoryValidationException("query must contain a word with at least two letters or digits.");
        }
        var concepts = new List<Concept>();
        foreach (var id in EnumerateConceptIds())
        {
            if (excludedDirectory is not null &&
                (PathHelper.ArePathsEqual(ResolveFile(id), ResolveFile(excludedDirectory)) ||
                 PathHelper.IsPathWithinDirectory(ResolveFile(id), ResolveDirectory(excludedDirectory))))
            {
                continue;
            }
            ct.ThrowIfCancellationRequested();
            concepts.Add(ParseConcept(id, (await ReadTextAsync(id, ct).ConfigureAwait(false))!));
        }
        var documents = concepts.Select(concept => new LexicalSearchDocument(concept.Id,
            DisplayTitle(concept), $"{MetadataText(concept, "description")}\n{concept.Body}",
            concept.Metadata.TryGetValue("tags", out var tags) && tags is IEnumerable<object> values
                ? values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).ToArray() : [])).ToArray();
        var matches = LexicalSearchStrategy.Search(query, documents, documents.Length);
        const string scope = "Scope: saved concept titles, descriptions, tags and bodies (lexical).";
        if (matches.Count == 0) { return $"No matching concepts.\n{scope}"; }
        var byId = concepts.ToDictionary(concept => concept.Id, StringComparer.Ordinal);
        return scope + "\n" + string.Join("\n", matches.Select(id =>
            $"conceptId: {id} | document: {LinkPath(id)} | {DisplayTitle(byId[id])}: {MetadataText(byId[id], "description")}"));
    }

    /// <summary>Creates or updates one concept while preserving metadata not supplied by the writer.</summary>
    public Task<string> SaveAsync(string conceptId, IReadOnlyDictionary<string, object?> frontmatter, string content,
        CancellationToken ct = default) => MutateAsync(async () =>
    {
        var id = ResolveConceptId(conceptId);
        var existing = await ReadTextAsync(id, CancellationToken.None).ConfigureAwait(false);
        var fields = existing is null ? new Dictionary<string, object?>() : ParseConcept(id, existing).Metadata;
        ApplyMetadata(fields, frontmatter);
        return await CommitConceptAsync(id, fields, content, existing is not null).ConfigureAwait(false);
    }, ct);

    /// <summary>Appends body text while retaining an existing concept's metadata and complete body.</summary>
    public Task<string> AppendAsync(string conceptId, IReadOnlyDictionary<string, object?> frontmatter, string content,
        CancellationToken ct = default) => MutateAsync(async () =>
    {
        var id = ResolveConceptId(conceptId);
        var existing = await ReadTextAsync(id, CancellationToken.None).ConfigureAwait(false);
        var concept = existing is null ? null : ParseConcept(id, existing);
        var fields = concept?.Metadata ?? new Dictionary<string, object?>();
        if (concept is null) { ApplyMetadata(fields, frontmatter); }
        return await CommitConceptAsync(id, fields, (concept?.Body ?? string.Empty) + content, existing is not null).ConfigureAwait(false);
    }, ct);

    /// <summary>Deletes one concept, maintains its indexes and reports surviving links without cascading deletion.</summary>
    public Task<string> DeleteAsync(string conceptId, CancellationToken ct = default) => MutateAsync(async () =>
    {
        var id = ResolveConceptId(conceptId);
        if (!File.Exists(ResolveFile(id))) { return $"Concept not found: {id}."; }
        var references = new List<string>();
        foreach (var otherId in EnumerateConceptIds().Where(candidate => !PathHelper.ArePathsEqual(ResolveFile(candidate), ResolveFile(id))))
        {
            var concept = ParseConcept(otherId, (await ReadTextAsync(otherId, CancellationToken.None).ConfigureAwait(false))!);
            if (GetLinks(concept).Any(link => PathHelper.ArePathsEqual(ResolveFile(link), ResolveFile(id)))) { references.Add(otherId); }
        }
        await CaptureAsync(id).ConfigureAwait(false);
        File.Delete(ResolveFile(id));
        await RebuildIndexesCoreAsync(id, new Dictionary<string, string>()).ConfigureAwait(false);
        return $"Deleted concept: {id}.\n" + (references.Count == 0
            ? "No remaining inbound links." : "Remaining inbound links: " + string.Join(", ", references));
    }, ct);

    /// <summary>Validates a complete user-edited concept before committing its encrypted file and indexes.</summary>
    public Task<string> EditDocumentAsync(string document, string content, CancellationToken ct = default) => MutateAsync(async () =>
    {
        var id = ResolveReference(document);
        if (IsReserved(id))
        {
            throw new AgentMemoryValidationException("Derived index and log files cannot be edited. Edit a concept document instead.");
        }
        var concept = ParseConcept(id, content);
        return await CommitConceptAsync(id, concept.Metadata, concept.Body, File.Exists(ResolveFile(id))).ConfigureAwait(false);
    }, ct);

    /// <summary>Reconciles derived indexes after an authorized edit through another existing file surface.</summary>
    public Task RebuildIndexesAsync(string document, CancellationToken ct = default) => MutateAsync(async () =>
    {
        var id = ResolveReference(document);
        if (!IsReserved(id) && await ReadTextAsync(id, CancellationToken.None).ConfigureAwait(false) is { } text)
        {
            _ = ParseConcept(id, text);
        }
        await RebuildIndexesCoreAsync(id, new Dictionary<string, string>()).ConfigureAwait(false);
        return "Indexes reconciled.";
    }, ct);

    /// <summary>Lists actual concept IDs under an authorized directory.</summary>
    public IReadOnlyList<string> ListConceptIds(string directory) =>
        EnumerateConceptIds().Where(id => PathHelper.IsPathWithinDirectory(ResolveFile(id), ResolveDirectory(directory))).ToArray();

    private async Task<string> CommitConceptAsync(string id, Dictionary<string, object?> fields, string body, bool existed)
    {
        RequireType(fields);
        var assembly = typeof(EncryptedKnowledgeStore).Assembly.GetName();
        fields["generated"] = new Dictionary<string, object?>
        {
            ["by"] = $"{assembly.Name}/{assembly.Version}",
            ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
        var text = MarkdownFrontmatter.Combine(_yaml.Serialize(fields), body);
        ValidateSize(text, "content");
        await RebuildIndexesCoreAsync(id, new Dictionary<string, string> { [id] = text }).ConfigureAwait(false);
        return $"{(existed ? "Updated" : "Created")} conceptId: {id}\ndocument: {LinkPath(id)}";
    }

    private void ApplyMetadata(Dictionary<string, object?> fields, IReadOnlyDictionary<string, object?> supplied)
    {
        if (supplied.ContainsKey("generated") || supplied.ContainsKey("verified"))
        {
            throw new AgentMemoryValidationException("frontmatter.generated and frontmatter.verified cannot be supplied by the agent.");
        }
        // JSON values from function arguments become ordinary YAML scalars and collections at the format boundary.
        try
        {
            foreach (var field in _yaml.DeserializeMapping(JsonSerializer.Serialize(supplied))) { fields[field.Key] = field.Value; }
        }
        catch (Exception ex) when (ex is YamlException or InvalidDataException or JsonException)
        {
            throw new AgentMemoryValidationException("frontmatter must be a metadata object with serializable values.");
        }
    }

    private async Task RebuildIndexesCoreAsync(string changedId, Dictionary<string, string> pending)
    {
        var directory = ParentDirectory(changedId);
        while (true)
        {
            var indexId = directory.Length == 0 ? "index" : directory + "/index";
            pending[indexId] = await BuildIndexAsync(directory, pending, CancellationToken.None).ConfigureAwait(false);
            if (directory.Length == 0) { break; }
            directory = ParentDirectory(directory);
        }
        // Check every resulting file before any admitted save changes its concept or index.
        foreach (var file in pending) { ValidateSize(file.Value, file.Key + ".md"); }
        foreach (var file in pending)
        {
            await CaptureAsync(file.Key).ConfigureAwait(false);
            await EncryptedFileContent.WriteAllTextAsync(_userId, ResolveFile(file.Key), file.Value, _protector).ConfigureAwait(false);
        }
    }

    private async Task<string> BuildIndexAsync(string directory, IReadOnlyDictionary<string, string> pending, CancellationToken ct)
    {
        var path = ResolveDirectory(directory);
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*" + FileSuffix).Select(FileId).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pending.Keys.Where(id => PathHelper.ArePathsEqual(ResolveDirectory(ParentDirectory(id)), path)))
        {
            // Staged paths replace native file aliases so their current metadata has one index entry.
            files.RemoveWhere(existing => PathHelper.ArePathsEqual(ResolveFile(existing), ResolveFile(id)));
            files.Add(id);
        }
        var directories = Directory.Exists(path)
            ? Directory.EnumerateDirectories(path).Select(child => Path.GetRelativePath(_root, child).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in pending.Keys)
        {
            var relative = directory.Length == 0 ? id : id.StartsWith(directory + "/", StringComparison.Ordinal) ? id[(directory.Length + 1)..] : string.Empty;
            var separator = relative.IndexOf('/');
            if (separator >= 0)
            {
                var child = (directory.Length == 0 ? string.Empty : directory + "/") + relative[..separator];
                if (!directories.Any(existing => PathHelper.ArePathsEqual(ResolveDirectory(existing), ResolveDirectory(child))))
                {
                    directories.Add(child);
                }
            }
        }
        var index = new StringBuilder();
        if (directory.Length == 0) { index.AppendLine("---\nokf_version: \"0.2\"\n---"); }
        index.AppendLine().AppendLine("# " + (directory.Length == 0 ? Path.GetFileName(_root) : Path.GetFileName(directory)));
        if (directories.Count > 0)
        {
            index.AppendLine().AppendLine("## Directories");
            foreach (var child in directories.Order(StringComparer.Ordinal))
            {
                _ = ResolveDirectory(child);
                index.AppendLine($"- [{InlineText(Path.GetFileName(child))}]({LinkPath(child + "/index")})");
            }
        }
        var concepts = files.Where(id => !IsReserved(id)).Order(StringComparer.Ordinal).ToArray();
        if (concepts.Length > 0) { index.AppendLine().AppendLine("## Concepts"); }
        foreach (var id in concepts)
        {
            var text = pending.TryGetValue(id, out var staged) ? staged : await ReadTextAsync(id, ct).ConfigureAwait(false);
            var concept = ParseConcept(id, text!);
            var description = MetadataText(concept, "description");
            index.AppendLine($"- [{InlineText(DisplayTitle(concept))}]({LinkPath(id)})" +
                (description.Length == 0 ? string.Empty : ": " + InlineText(description)));
        }
        if (directories.Count == 0 && concepts.Length == 0) { index.AppendLine().AppendLine("No saved concepts."); }
        return index.ToString();
    }

    private IEnumerable<string> EnumerateConceptIds(string directory = "")
    {
        var path = ResolveDirectory(directory);
        if (!Directory.Exists(path)) { yield break; }
        foreach (var file in Directory.EnumerateFiles(path, "*" + FileSuffix))
        {
            var id = FileId(file);
            _ = ResolveFile(id);
            if (!IsReserved(id)) { yield return id; }
        }
        foreach (var child in Directory.EnumerateDirectories(path))
        {
            var relative = Path.GetRelativePath(_root, child).Replace('\\', '/');
            foreach (var id in EnumerateConceptIds(relative)) { yield return id; }
        }
    }

    private Concept ParseConcept(string id, string text)
    {
        try
        {
            var (yaml, body) = MarkdownFrontmatter.Split(text);
            if (string.IsNullOrWhiteSpace(yaml)) { throw new InvalidDataException(); }
            var fields = new Dictionary<string, object?>(_yaml.DeserializeMapping(yaml));
            RequireType(fields);
            return new Concept(id, fields, body);
        }
        catch (Exception ex) when (ex is InvalidDataException or YamlException)
        {
            // Parser exceptions can contain document text, so expose only the affected field and reference.
            throw new AgentMemoryValidationException($"frontmatter in {id}.md must be a YAML mapping with a nonempty string type.");
        }
    }

    private static void RequireType(IReadOnlyDictionary<string, object?> fields)
    {
        if (!fields.TryGetValue("type", out var type) || type is not string text || string.IsNullOrWhiteSpace(text))
        {
            throw new AgentMemoryValidationException("frontmatter.type must be a nonempty string.");
        }
    }

    private IEnumerable<string> GetLinks(Concept concept)
    {
        foreach (Match match in MarkdownLinks.Matches(concept.Body))
        {
            var target = match.Groups["target"].Value.Split('#', 2)[0];
            if (!target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                !target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out _))
            {
                continue;
            }
            var relative = target.StartsWith('/') ? target.TrimStart('/') : ParentDirectory(concept.Id) + "/" + target;
            if (ParentDirectory(concept.Id).Length == 0 && !target.StartsWith('/')) { relative = target; }
            string id;
            try { id = ResolveReference(Uri.UnescapeDataString(relative)); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or AgentMemoryValidationException)
            {
                // A saved link never grants access beyond this bundle.
                continue;
            }
            yield return id;
        }
    }

    private async Task<string?> ReadTextAsync(string id, CancellationToken ct)
    {
        var text = await EncryptedFileContent.ReadAllTextAsync(_userId, ResolveFile(id), _protector, ct).ConfigureAwait(false);
        if (text is not null) { ValidateSize(text, id + ".md"); }
        return text;
    }

    private string ResolveReference(string reference)
    {
        var value = reference.Trim().Replace('\\', '/');
        // Rooted Markdown links use URI escaping, while bare concept IDs retain their literal characters.
        if (value.StartsWith('/')) { value = Uri.UnescapeDataString(value).TrimStart('/'); }
        if (value.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) { value = value[..^3]; }
        if (value.Length == 0 || Path.IsPathRooted(value) || Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new AgentMemoryValidationException("document must be a bundle-relative ID or Markdown path.");
        }
        return FileId(ResolveFile(value));
    }

    private string ResolveFile(string id) => ResolveDirectory(id + FileSuffix);

    private string ResolveDirectory(string relative)
    {
        if (!PathHelper.TryResolveContainedPath(_root, PathHelper.ToPlatformRelativePath(relative), out var path) ||
            !PathHelper.TryResolveContainedPath(_userRoot, Path.GetRelativePath(_userRoot, path), out _))
        {
            throw new UnauthorizedAccessException("Knowledge path escapes the authorized bundle or contains a linked path.");
        }
        return path;
    }

    private string FileId(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/')[..^FileSuffix.Length];
    private Task CaptureAsync(string id) => _fileBackups.CaptureBeforeMutationAsync(
        _userId, Path.GetRelativePath(_userRoot, ResolveFile(id)).Replace('\\', '/'), CancellationToken.None);
    private static bool IsReserved(string id) =>
        Path.GetFileName(id).Equals("index", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(id).Equals("log", StringComparison.OrdinalIgnoreCase);
    private static string ParentDirectory(string id) => Path.GetDirectoryName(id)?.Replace('\\', '/') ?? string.Empty;
    private static string LinkPath(string id) => "/" + string.Join("/", (id + ".md").Split('/').Select(Uri.EscapeDataString));
    private static string InlineText(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace("[", "\\[").Replace("]", "\\]");
    private static string MetadataText(Concept concept, string key) =>
        concept.Metadata.TryGetValue(key, out var value) && value is string text ? text : string.Empty;
    private static string DisplayTitle(Concept concept) =>
        MetadataText(concept, "title") is { Length: > 0 } title ? title : Path.GetFileName(concept.Id);

    private void ValidateSize(string text, string field)
    {
        if (Encoding.UTF8.GetByteCount(text) > _maxTextSizeBytes)
        {
            throw new AgentMemoryValidationException($"{field} exceeds {_maxTextSizeBytes} UTF-8 bytes. Shorten the complete document.");
        }
    }

    private async Task<string> MutateAsync(Func<Task<string>> mutation, CancellationToken ct)
    {
        var gate = MutationGates.GetOrAdd(_userId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await mutation().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>Keeps a parsed concept's metadata and body together while its index is derived.</summary>
    private sealed record Concept(string Id, Dictionary<string, object?> Metadata, string Body);
}
