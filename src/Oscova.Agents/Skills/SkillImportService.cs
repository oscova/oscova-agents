using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Skills;

/// <summary>
/// Stages, validates, and installs skills into the on-disk skills directory.
/// </summary>
public sealed class SkillImportService : ISkillImportService
{
    private const string SkillFileName = "SKILL.md";
    private const string TempStagingDirectoryName = ".import-staging";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ImportGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock ImportRootLock = new();
    private static readonly HashSet<string> ActiveImportRoots = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, string> _skillsRootResolver;
    private readonly ILogger<SkillImportService> _logger;

    /// <summary>Creates a skill importer beneath a caller-selected skills root.</summary>
    public SkillImportService(
        Func<string, string> skillsRootResolver,
        ILogger<SkillImportService> logger)
    {
        _skillsRootResolver = skillsRootResolver ?? throw new ArgumentNullException(nameof(skillsRootResolver));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SkillImportResult> ImportAsync(
        string userId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        string skillsRoot = ResolveSkillsRoot(userId);
        return await ImportCoreAsync(
            skillsRoot,
            fileName,
            content,
            ImportConflictMode.FailIfExists,
            expectedSkillName: null,
            allowReservedOscovaMetadata: false,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SkillImportResult> ImportAgentAuthoredMarkdownAsync(
        string userId,
        string content,
        AgentAuthoredSkillContext authoring,
        CancellationToken cancellationToken = default)
    {
        string skillsRoot = ResolveSkillsRoot(userId);
        var normalized = AgentAuthoredSkillFrontmatter.NormalizeForCreate(content, authoring);
        await using var stream = CreateUtf8Stream(normalized.Markdown);
        return await ImportCoreAsync(
            skillsRoot,
            $"{normalized.Name}.md",
            stream,
            ImportConflictMode.FailIfExists,
            expectedSkillName: normalized.Name,
            allowReservedOscovaMetadata: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SkillImportResult> ReplaceAgentAuthoredMarkdownAsync(
        string userId,
        string skillName,
        string content,
        AgentAuthoredSkillContext authoring,
        CancellationToken cancellationToken = default)
    {
        string skillsRoot = ResolveSkillsRoot(userId);
        skillName = ValidateSkillDirectorySegment(skillName, nameof(skillName));

        var existingMarkdown = await ReadExistingAgentAuthoredSkillAsync(
            skillsRoot,
            skillName,
            cancellationToken).ConfigureAwait(false);

        if (!AgentAuthoredSkillFrontmatter.TryReadProvenance(existingMarkdown, out var provenance))
        {
            throw new SkillAuthorshipException(skillName);
        }

        var normalized = AgentAuthoredSkillFrontmatter.NormalizeForUpdate(
            content,
            skillName,
            authoring,
            provenance);

        await using var stream = CreateUtf8Stream(normalized.Markdown);
        return await ImportCoreAsync(
            skillsRoot,
            $"{skillName}.md",
            stream,
            ImportConflictMode.ReplaceExisting,
            expectedSkillName: skillName,
            allowReservedOscovaMetadata: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs an authorized package while preserving trusted provenance and validating its exact skill name.</summary>
    public Task<SkillImportResult> ImportTrustedPackageAsync(
        string userId,
        string skillName,
        Stream content,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        // The caller authorizes distribution. Archive, path and size validation still run here.
        skillName = ValidateSkillDirectorySegment(skillName, nameof(skillName));
        return ImportCoreAsync(
            ResolveSkillsRoot(userId),
            skillName + ".zip",
            content,
            replaceExisting ? ImportConflictMode.ReplaceExisting : ImportConflictMode.FailIfExists,
            skillName,
            allowReservedOscovaMetadata: true,
            cancellationToken);
    }

    /// <summary>
    /// Controls what happens when the incoming skill name already exists on disk.
    /// </summary>
    private enum ImportConflictMode
    {
        FailIfExists,
        ReplaceExisting
    }

    /// <summary>Exports one installed skill and its support files using the importer's path and size limits.</summary>
    public async Task<byte[]> ExportPackageAsync(string userId, string skillName, CancellationToken ct = default)
    {
        skillName = ValidateSkillDirectorySegment(skillName, nameof(skillName));
        var root = ResolveSkillsRoot(userId);
        var directory = Path.Combine(root, skillName);
        EnsureReplaceTargetSafe(root, directory, skillName);
        using var output = new MemoryStream();
        long expanded = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.TryPop(out var current))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(current))
                {
                    ct.ThrowIfCancellationRequested();
                    if (PathHelper.HasSymlinkInPath(path, root))
                    {
                        throw new InvalidOperationException("Skill packages cannot contain symbolic links.");
                    }
                    if (Directory.Exists(path)) { pending.Push(path); continue; }
                    var entry = archive.CreateEntry(skillName + "/" +
                        PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(directory, path)));
                    await using var input = File.OpenRead(path);
                    await using var target = entry.Open();
                    var buffer = new byte[81920];
                    int count;
                    while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                    {
                        expanded += count;
                        if (expanded > SkillImportLimits.MaxUncompressedZipSizeBytes)
                        {
                            throw new ArgumentException("The skill exceeds the expanded package size limit.");
                        }
                        await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    }
                }
            }
        }
        if (output.Length > SkillImportLimits.MaxUploadSizeBytes)
        {
            throw new ArgumentException("The skill exceeds the compressed package size limit.");
        }
        return output.ToArray();
    }

    /// <summary>Removes one installed package after its owner has authorized removal.</summary>
    public void RemoveTrustedPackage(string userId, string skillName)
    {
        skillName = ValidateSkillDirectorySegment(skillName, nameof(skillName));
        var root = ResolveSkillsRoot(userId);
        var directory = Path.Combine(root, skillName);
        if (!Directory.Exists(directory)) { return; }
        EnsureReplaceTargetSafe(root, directory, skillName);
        Directory.Delete(directory, recursive: true);
    }
    private async Task<SkillImportResult> ImportCoreAsync(
        string skillsRoot,
        string fileName,
        Stream content,
        ImportConflictMode conflictMode,
        string? expectedSkillName,
        bool allowReservedOscovaMetadata,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("A skill file is required.");
        }

        expectedSkillName = expectedSkillName is null
            ? null
            : ValidateSkillDirectorySegment(expectedSkillName, "expected skill name");

        string extension = Path.GetExtension(fileName);
        bool isMarkdown = extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
        bool isZip = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        if (!isMarkdown && !isZip)
        {
            throw new ArgumentException("Only .md and .zip skill imports are supported.");
        }

        string importRoot = CreateImportRoot(skillsRoot);
        SemaphoreSlim importGate = ImportGates.GetOrAdd(
            skillsRoot,
            static _ => new SemaphoreSlim(1, 1));
        bool importGateHeld = false;
        try
        {
            string uploadPath = Path.Combine(importRoot, "upload" + extension.ToLowerInvariant());
            await SaveUploadAsync(content, uploadPath, cancellationToken).ConfigureAwait(false);

            string normalizedRoot = Path.Combine(importRoot, "normalized");
            Directory.CreateDirectory(normalizedRoot);

            string normalizedSkillDirectory = isMarkdown
                ? await MaterializeMarkdownAsync(uploadPath, normalizedRoot, cancellationToken).ConfigureAwait(false)
                : await MaterializeArchiveAsync(uploadPath, importRoot, normalizedRoot, cancellationToken).ConfigureAwait(false);

            string skillContent = await File.ReadAllTextAsync(
                Path.Combine(normalizedSkillDirectory, SkillFileName),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);

            if (!allowReservedOscovaMetadata)
            {
                // Only trusted agent-authoring callers may stamp local Oscova provenance.
                AgentAuthoredSkillFrontmatter.ThrowIfReservedMetadataPresent(skillContent);
            }

            // Reject unsupported resources, but leave script executability to runtime discovery.
            ValidateDiscoveredFiles(normalizedSkillDirectory);

            // SkillDiscoverer remains the final authority for slug, description, and directory matching.
            DiscoveredSkill discoveredSkill = DiscoverSingleSkill(normalizedRoot);
            string discoveredName = ValidateSkillDirectorySegment(discoveredSkill.Name, "discovered skill name");
            if (expectedSkillName is not null &&
                !string.Equals(discoveredName, expectedSkillName, StringComparison.Ordinal))
            {
                throw new ArgumentException($"SKILL.md name must match '{expectedSkillName}'.");
            }

            string destinationDirectory = Path.Combine(skillsRoot, discoveredName);
            string conflictMessage = $"A skill named '{discoveredName}' is already installed.";
            await importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            importGateHeld = true;
            if (conflictMode == ImportConflictMode.FailIfExists && Directory.Exists(destinationDirectory))
            {
                throw new SkillImportConflictException(conflictMessage);
            }

            if (conflictMode == ImportConflictMode.ReplaceExisting)
            {
                EnsureReplaceTargetSafe(skillsRoot, destinationDirectory, discoveredName);
            }

            SkillImportResult result = CollectCompatibilityFacts(normalizedSkillDirectory, skillContent, discoveredSkill);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationDirectory)!);
            if (conflictMode == ImportConflictMode.ReplaceExisting)
            {
                ReplaceSkillDirectory(normalizedSkillDirectory, destinationDirectory, importRoot);
            }
            else
            {
                // Staging and destination share the selected skills root's parent directory.
                try
                {
                    Directory.Move(normalizedSkillDirectory, destinationDirectory);
                }
                catch (IOException) when (Directory.Exists(destinationDirectory))
                {
                    throw new SkillImportConflictException(conflictMessage);
                }
            }

            return result;
        }
        finally
        {
            if (importGateHeld)
            {
                importGate.Release();
            }

            TryDeleteDirectory(importRoot);
            lock (ImportRootLock)
            {
                ActiveImportRoots.Remove(importRoot);
            }
        }
    }

    private string ResolveSkillsRoot(string userId) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(_skillsRootResolver(userId)));

    private string CreateImportRoot(string skillsRoot)
    {
        string parent = Path.GetDirectoryName(skillsRoot)
            ?? throw new ArgumentException("The skills root must have a parent directory.");
        string stagingRoot = Path.Combine(parent, TempStagingDirectoryName);

        lock (ImportRootLock)
        {
            if (Directory.Exists(stagingRoot))
            {
                // Sweep abandoned staging without disturbing concurrent imports in this process.
                foreach (string directory in Directory.EnumerateDirectories(stagingRoot))
                {
                    if (!ActiveImportRoots.Contains(directory))
                    {
                        TryDeleteDirectory(directory);
                    }
                }
            }

            string importRoot = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importRoot);
            ActiveImportRoots.Add(importRoot);
            return importRoot;
        }
    }

    private static async Task SaveUploadAsync(Stream content, string uploadPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(uploadPath)!);
        FileStream fileStream = new(uploadPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (fileStream.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                totalBytesRead += bytesRead;
                if (totalBytesRead > SkillImportLimits.MaxUploadSizeBytes)
                {
                    throw new InvalidOperationException(
                        $"Skill package exceeds the maximum upload size of {SkillImportLimits.MaxUploadSizeBytes / (1024 * 1024)} MB.");
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> MaterializeMarkdownAsync(
        string uploadPath,
        string normalizedRoot,
        CancellationToken cancellationToken)
    {
        string content = await File.ReadAllTextAsync(uploadPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        string name = ReadSkillName(content, uploadPath);

        string normalizedSkillDirectory = Path.Combine(normalizedRoot, name);
        Directory.CreateDirectory(normalizedSkillDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(normalizedSkillDirectory, SkillFileName),
            content,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        return normalizedSkillDirectory;
    }

    private static async Task<string> MaterializeArchiveAsync(
        string uploadPath,
        string importRoot,
        string normalizedRoot,
        CancellationToken cancellationToken)
    {
        string extractedRoot = Path.Combine(importRoot, "extracted");
        Directory.CreateDirectory(extractedRoot);

        await ExtractArchiveSafelyAsync(uploadPath, extractedRoot, cancellationToken).ConfigureAwait(false);

        List<string> topLevelDirectories = [.. Directory.EnumerateDirectories(extractedRoot)];
        List<string> topLevelFiles = [.. Directory.EnumerateFiles(extractedRoot)];

        // Wrapper-dir shape: SkillDiscoverer later rejects any name/directory mismatch.
        if (topLevelDirectories.Count == 1 && topLevelFiles.Count == 0)
        {
            string wrapperDirectory = topLevelDirectories[0];
            if (!File.Exists(Path.Combine(wrapperDirectory, SkillFileName)))
            {
                throw new ArgumentException("The archive wrapper directory does not contain SKILL.md at its root.");
            }

            string wrapperName = ValidateSkillDirectorySegment(Path.GetFileName(wrapperDirectory), "archive wrapper directory");
            string normalizedSkillDirectory = Path.Combine(normalizedRoot, wrapperName);
            Directory.Move(wrapperDirectory, normalizedSkillDirectory);
            return normalizedSkillDirectory;
        }

        string rootSkillFile = Path.Combine(extractedRoot, SkillFileName);
        if (File.Exists(rootSkillFile))
        {
            bool hasExtraRootFiles = topLevelFiles.Any(filePath =>
                !string.Equals(Path.GetFileName(filePath), SkillFileName, StringComparison.OrdinalIgnoreCase));
            if (hasExtraRootFiles)
            {
                throw new ArgumentException("The archive root contains files alongside SKILL.md that are not supported.");
            }

            string content = await File.ReadAllTextAsync(rootSkillFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            string name = ReadSkillName(content, rootSkillFile);

            string normalizedSkillDirectory = Path.Combine(normalizedRoot, name);
            Directory.CreateDirectory(normalizedSkillDirectory);

            foreach (string entryPath in Directory.EnumerateFileSystemEntries(extractedRoot))
            {
                string destinationPath = Path.Combine(normalizedSkillDirectory, Path.GetFileName(entryPath));
                if (Directory.Exists(entryPath))
                {
                    Directory.Move(entryPath, destinationPath);
                }
                else
                {
                    File.Move(entryPath, destinationPath);
                }
            }

            return normalizedSkillDirectory;
        }

        throw new ArgumentException("The archive must contain a single skill with SKILL.md at the wrapper directory root or the archive root.");
    }

    private static async Task ExtractArchiveSafelyAsync(
        string uploadPath,
        string extractedRoot,
        CancellationToken cancellationToken)
    {
        FileStream zipStream = File.OpenRead(uploadPath);
        await using (zipStream.ConfigureAwait(false))
        {
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);

            var extractionItems = ZipArchiveExtractionPlan.Create(
                archive,
                extractedRoot,
                SkillImportLimits.MaxUncompressedZipSizeBytes,
                entry =>
                {
                    string relativePath = PathHelper.ToSlashSeparatedRelativePath(entry.FullName);
                    return ShouldSkipArchiveEntry(relativePath) ? null : relativePath;
                });

            foreach (var item in extractionItems)
            {
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(item.DestinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
                Stream sourceStream = item.Entry.Open();
                await using (sourceStream.ConfigureAwait(false))
                {
                    await using FileStream destinationStream = new(
                        item.DestinationPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None);
                    await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<string> ReadExistingAgentAuthoredSkillAsync(
        string skillsRoot,
        string skillName,
        CancellationToken cancellationToken)
    {
        string skillDirectory = Path.Combine(skillsRoot, skillName);
        EnsureReplaceTargetSafe(skillsRoot, skillDirectory, skillName);

        string skillFilePath = Path.Combine(skillDirectory, SkillFileName);
        if (!File.Exists(skillFilePath))
        {
            throw new FileNotFoundException($"Skill '{skillName}' does not contain SKILL.md.");
        }

        if (PathHelper.HasSymlinkInPath(Path.GetFullPath(skillFilePath), NormalizeDirectory(skillsRoot)))
        {
            throw new InvalidOperationException($"Skill '{skillName}' cannot be updated because its path is not safe.");
        }

        return await File.ReadAllTextAsync(skillFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureReplaceTargetSafe(string skillsRoot, string destinationDirectory, string skillName)
    {
        string normalizedSkillsRoot = NormalizeDirectory(skillsRoot);
        string normalizedDestination = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedParent = (Path.GetDirectoryName(normalizedDestination) ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.Equals(normalizedSkillsRoot, normalizedParent, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        if (!Directory.Exists(normalizedDestination))
        {
            throw new FileNotFoundException($"Skill '{skillName}' is not installed.");
        }

        if (PathHelper.HasSymlinkInPath(normalizedDestination, normalizedSkillsRoot))
        {
            throw new InvalidOperationException($"Skill '{skillName}' cannot be updated because its path is not safe.");
        }
    }

    private static DiscoveredSkill DiscoverSingleSkill(string normalizedRoot)
    {
        IReadOnlyList<DiscoveredSkill> discoveredSkills = new SkillDiscoverer().Discover(normalizedRoot);
        if (discoveredSkills.Count != 1)
        {
            throw new ArgumentException("The import must contain exactly one valid skill.");
        }

        return discoveredSkills[0];
    }

    private static string ReadSkillName(string content, string sourceLabel)
    {
        if (!SkillFrontmatter.TryRead(content, out IReadOnlyDictionary<string, string> values))
        {
            throw new ArgumentException($"SKILL.md at '{sourceLabel}' does not contain valid YAML frontmatter delimited by '---'.");
        }

        if (!values.TryGetValue("name", out string? name) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"SKILL.md at '{sourceLabel}' is missing a 'name' field in frontmatter.");
        }

        return ValidateSkillDirectorySegment(name, sourceLabel);
    }

    private static string ValidateSkillDirectorySegment(string name, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            Path.IsPathRooted(name) ||
            name is "." or ".." ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Skill name from '{sourceLabel}' must be a single directory name.");
        }

        return name;
    }

    private static void ValidateDiscoveredFiles(string normalizedSkillDirectory)
    {
        (string Directory, IReadOnlyList<string> AllowedExtensions)[] resourceRules =
        [
            ("references", SkillFileDiscoveryOptions.ResourceExtensions),
            ("assets", SkillFileDiscoveryOptions.ResourceExtensions)
        ];

        foreach (string filePath in Directory.EnumerateFiles(normalizedSkillDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(normalizedSkillDirectory, filePath));
            if (string.Equals(relativePath, SkillFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string topLevelSegment = relativePath.Split('/')[0];
            string extension = Path.GetExtension(filePath);

            foreach (var (directoryName, allowedExtensions) in resourceRules)
            {
                if (!string.Equals(topLevelSegment, directoryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Unsupported file '{relativePath}'. Allowed extensions under '{directoryName}/': {string.Join(", ", allowedExtensions)}.");
                }

                break;
            }
        }
    }

    private static SkillImportResult CollectCompatibilityFacts(
        string normalizedSkillDirectory,
        string skillContent,
        DiscoveredSkill discoveredSkill)
    {
        var allowedTopLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SkillFileName };
        foreach (string directoryName in SkillFileDiscoveryOptions.ScriptDirectories)
        {
            allowedTopLevelNames.Add(directoryName);
        }
        foreach (string directoryName in SkillFileDiscoveryOptions.ResourceDirectories)
        {
            allowedTopLevelNames.Add(directoryName);
        }

        List<string> extraTopLevelPaths =
        [
            .. Directory.EnumerateFileSystemEntries(normalizedSkillDirectory)
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !allowedTopLevelNames.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        ];

        bool declaresAllowedTools = false;
        var ignoredFrontmatterKeys = new List<string>();
        if (SkillFrontmatter.TryRead(skillContent, out IReadOnlyDictionary<string, string> frontmatter))
        {
            declaresAllowedTools = frontmatter.ContainsKey("allowed-tools");

            if (frontmatter.ContainsKey("user-invocable"))
            {
                ignoredFrontmatterKeys.Add("user-invocable");
            }
            if (frontmatter.ContainsKey("disable-model-invocation"))
            {
                ignoredFrontmatterKeys.Add("disable-model-invocation");
            }
        }

        return new SkillImportResult
        {
            Name = discoveredSkill.Name,
            Description = discoveredSkill.Description,
            UnusedTopLevelPaths = extraTopLevelPaths,
            DeclaresAllowedTools = declaresAllowedTools,
            IgnoredFrontmatterKeys = ignoredFrontmatterKeys
        };
    }

    private static void ReplaceSkillDirectory(string sourceDirectory, string destinationDirectory, string importRoot)
    {
        string backupDirectory = Path.Combine(importRoot, "backup-existing");
        Directory.Move(destinationDirectory, backupDirectory);

        try
        {
            // Both directories share the selected skills root's parent directory.
            Directory.Move(sourceDirectory, destinationDirectory);
        }
        catch
        {
            if (!Directory.Exists(destinationDirectory) && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, destinationDirectory);
            }

            throw;
        }
    }

    private static MemoryStream CreateUtf8Stream(string content) =>
        new(Encoding.UTF8.GetBytes(content));

    private static bool ShouldSkipArchiveEntry(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        if (relativePath.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(Path.GetFileName(relativePath), ".DS_Store", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string directoryPath) =>
        Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void TryDeleteDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temporary skill import directory '{DirectoryPath}'.", directoryPath);
        }
    }
}
