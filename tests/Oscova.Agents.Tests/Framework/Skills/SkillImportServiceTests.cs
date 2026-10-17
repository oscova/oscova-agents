using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Persistence;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Tests.Framework.Skills;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SkillImportServiceTestCollection
{
    public const string Name = "Skill import filesystem";
}

[Collection(SkillImportServiceTestCollection.Name)]
public sealed class SkillImportServiceTests : IDisposable
{
    private const string UserId = "user-1";

    private readonly string _tempRootPath = Path.Combine(Path.GetTempPath(), "oscova-skill-import-tests", Guid.NewGuid().ToString("N"));
    private string _skillsRootPath;
    private readonly SkillImportService _service;

    public SkillImportServiceTests()
    {
        _skillsRootPath = CreateSkillsRoot("first");
        _service = new SkillImportService(_ => _skillsRootPath, NullLogger<SkillImportService>.Instance);
    }

    [Fact]
    public async Task ImportAsync_MarkdownSkill_InstallsSkillAndReturnsOriginalContent()
    {
        string markdown = """
            ---
            name: markdown-skill
            description: Markdown import test
            ---
            # Markdown Skill
            """;

        await using MemoryStream stream = CreateUtf8Stream(markdown);

        var result = await _service.ImportAsync(UserId, "markdown-skill.md", stream);

        Assert.Equal("markdown-skill", result.Name);
        Assert.Equal("Markdown import test", result.Description);

        string installedSkillPath = Path.Combine(_skillsRootPath, "markdown-skill", "SKILL.md");
        Assert.True(File.Exists(installedSkillPath));
        Assert.Equal(markdown.Replace("\r\n", "\n"), File.ReadAllText(installedSkillPath).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ImportAsync_RemovesStaleStagingDirectory()
    {
        string staleDirectory = Path.Combine(
            Path.GetDirectoryName(_skillsRootPath)!,
            ".import-staging",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staleDirectory);
        File.WriteAllText(Path.Combine(staleDirectory, "orphaned.txt"), "stale");
        const string markdown = """
            ---
            name: cleanup-skill
            description: Staging cleanup test
            ---
            # Cleanup Skill
            """;
        await using MemoryStream stream = CreateUtf8Stream(markdown);

        await _service.ImportAsync(UserId, "cleanup-skill.md", stream);

        Assert.False(Directory.Exists(staleDirectory));
        Assert.Empty(GetImportTempDirectories());
    }

    [Fact]
    public async Task ImportAsync_MarkdownSkillWithoutName_ThrowsArgumentException()
    {
        string markdown = """
            ---
            description: Missing name
            ---
            # Missing Name
            """;

        await using MemoryStream stream = CreateUtf8Stream(markdown);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ImportAsync(UserId, "missing-name.md", stream));
    }

    [Fact]
    public async Task ImportAsync_ZipSkillWithWrapperDirectory_InstallsToWrapperDirectoryName()
    {
        await using MemoryStream zipStream = CreateZipStream(
        [
            ("zip-skill/SKILL.md", """
                ---
                name: zip-skill
                description: Zip import test
                ---
                # Zip Skill
                """),
            ("zip-skill/scripts/run.py", "print('hello')"),
            ("zip-skill/assets/reference.txt", "reference")
        ]);

        var result = await _service.ImportAsync(UserId, "zip-skill.zip", zipStream);

        Assert.Equal("zip-skill", result.Name);

        string skillRoot = Path.Combine(_skillsRootPath, "zip-skill");
        Assert.True(File.Exists(Path.Combine(skillRoot, "scripts", "run.py")));
        Assert.True(File.Exists(Path.Combine(skillRoot, "assets", "reference.txt")));
    }

    [Fact]
    public async Task ImportAsync_ZipSkillWithRootSkillFile_NormalizesToFrontmatterName()
    {
        await using MemoryStream zipStream = CreateZipStream(
        [
            ("SKILL.md", """
                ---
                name: root-skill
                description: Root zip import test
                ---
                # Root Skill
                """),
            ("references/readme.txt", "reference")
        ]);

        var result = await _service.ImportAsync(UserId, "root-skill.zip", zipStream);

        Assert.Equal("root-skill", result.Name);
        string skillRoot = Path.Combine(_skillsRootPath, "root-skill");
        Assert.True(File.Exists(Path.Combine(skillRoot, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(skillRoot, "references", "readme.txt")));
    }

    [Fact]
    public async Task ImportAsync_ZipWithTraversalEntry_ThrowsAndCleansTempDirectory()
    {
        await using MemoryStream zipStream = CreateZipStream(
        [
            ("evil-skill/SKILL.md", """
                ---
                name: evil-skill
                description: Traversal test
                ---
                # Evil Skill
                """),
            ("../evil.txt", "escape")
        ]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ImportAsync(UserId, "evil-skill.zip", zipStream));

        Assert.False(Directory.Exists(Path.Combine(_skillsRootPath, "evil-skill")));
        Assert.Empty(GetImportTempDirectories());
    }

    [Fact]
    public async Task ImportAsync_WhenSkillAlreadyExists_ThrowsConflictException()
    {
        string skillRoot = Path.Combine(_skillsRootPath, "duplicate-skill");
        Directory.CreateDirectory(skillRoot);

        string markdown = """
            ---
            name: duplicate-skill
            description: Duplicate skill
            ---
            # Duplicate Skill
            """;

        await using MemoryStream stream = CreateUtf8Stream(markdown);

        await Assert.ThrowsAsync<SkillImportConflictException>(() =>
            _service.ImportAsync(UserId, "duplicate-skill.md", stream));

        Assert.True(Directory.Exists(skillRoot));
    }

    [Fact]
    public async Task ImportAsync_ConcurrentSameNameImports_ReportConflictForLoser()
    {
        const string SkillName = "concurrent-skill";
        byte[] package = CreateConcurrentImportPackage(SkillName);
        var gate = new AsyncGate(participantCount: 2);
        await using var firstStream = new CoordinatedReadStream(package, gate);
        await using var secondStream = new CoordinatedReadStream(package, gate);

        Task<Exception?> first = CaptureExceptionAsync(() =>
            _service.ImportAsync(UserId, $"{SkillName}.zip", firstStream));
        Task<Exception?> second = CaptureExceptionAsync(() =>
            _service.ImportAsync(UserId, $"{SkillName}.zip", secondStream));

        Exception?[] outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is SkillImportConflictException);
        Assert.True(File.Exists(Path.Combine(_skillsRootPath, SkillName, "SKILL.md")));
        Assert.Empty(GetImportTempDirectories());
    }

    [Fact]
    public async Task ImportAsync_WithFrameworkScriptExtension_InstallsScript()
    {
        await using MemoryStream zipStream = CreateZipStream(
        [
            ("framework-script-skill/SKILL.md", """
                ---
                name: framework-script-skill
                description: Framework script extension
                ---
                # Framework Script Skill
                """),
            ("framework-script-skill/scripts/run.ps1", "Write-Host 'hello'")
        ]);

        var result = await _service.ImportAsync(UserId, "framework-script-skill.zip", zipStream);

        Assert.Equal("framework-script-skill", result.Name);
        string installedScript = Path.Combine(
            _skillsRootPath,
            "framework-script-skill",
            "scripts",
            "run.ps1");
        Assert.True(File.Exists(installedScript));
    }

    [Fact]
    public async Task ImportAsync_WhenAllowedToolsFrontmatterIsPresent_ReturnsCompatibilityFact()
    {
        string markdown = """
            ---
            name: warning-skill
            description: Allowed tools warning test
            allowed-tools:
              - web__search
            ---
            # Warning Skill
            """;

        await using MemoryStream stream = CreateUtf8Stream(markdown);

        var result = await _service.ImportAsync(UserId, "warning-skill.md", stream);

        Assert.True(result.DeclaresAllowedTools);
    }

    [Fact]
    public async Task ImportAsync_OnFailure_CleansPerImportTempDirectory()
    {
        string markdown = """
            ---
            description: Missing name again
            ---
            # Missing Name
            """;

        await using MemoryStream stream = CreateUtf8Stream(markdown);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ImportAsync(UserId, "cleanup-check.md", stream));

        Assert.Empty(GetImportTempDirectories());
    }

    [Fact]
    public async Task ImportAsync_WhenConfiguredRootsChange_UsesCurrentRoots()
    {
        string firstRoot = _skillsRootPath;
        await using (MemoryStream firstStream = CreateUtf8Stream("""
            ---
            name: first-root-skill
            description: First root
            ---
            # First Root
            """))
        {
            await _service.ImportAsync(UserId, "first-root-skill.md", firstStream);
        }

        _skillsRootPath = CreateSkillsRoot("second");
        await using (MemoryStream secondStream = CreateUtf8Stream("""
            ---
            name: second-root-skill
            description: Second root
            ---
            # Second Root
            """))
        {
            await _service.ImportAsync(UserId, "second-root-skill.md", secondStream);
        }

        Assert.True(File.Exists(Path.Combine(firstRoot, "first-root-skill", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(firstRoot, "second-root-skill")));
        Assert.True(File.Exists(Path.Combine(_skillsRootPath, "second-root-skill", "SKILL.md")));
    }

    [Fact]
    public async Task ImportAsync_WhenMarkdownContainsReservedOscovaMetadata_ThrowsAndDoesNotInstall()
    {
        string markdown = """
            ---
            name: spoofed-markdown-skill
            description: Spoofed provenance
            metadata:
              oscova-authored-by: agent
              oscova-created-at: 2026-07-07T10:00:00Z
              oscova-updated-at: 2026-07-07T10:00:00Z
            ---
            # Spoofed Skill
            """;
        await using MemoryStream stream = CreateUtf8Stream(markdown);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ImportAsync(UserId, "spoofed-markdown-skill.md", stream));

        Assert.Contains("reserved Oscova provenance", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_skillsRootPath, "spoofed-markdown-skill")));
        Assert.Empty(GetImportTempDirectories());
    }

    [Fact]
    public async Task ImportAsync_WhenZipContainsReservedOscovaMetadata_ThrowsAndDoesNotInstall()
    {
        await using MemoryStream zipStream = CreateZipStream(
        [
            ("spoofed-zip-skill/SKILL.md", """
                ---
                name: spoofed-zip-skill
                description: Spoofed zip provenance
                metadata:
                  oscova-authored-by: agent
                  oscova-created-at: 2026-07-07T10:00:00Z
                  oscova-updated-at: 2026-07-07T10:00:00Z
                ---
                # Spoofed Zip Skill
                """)
        ]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ImportAsync(UserId, "spoofed-zip-skill.zip", zipStream));

        Assert.Contains("reserved Oscova provenance", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_skillsRootPath, "spoofed-zip-skill")));
        Assert.Empty(GetImportTempDirectories());
    }

    /// <summary>
    /// Proves agent-authored imports stamp trusted provenance and reject model spoofing.
    /// </summary>
    [Fact]
    public async Task ImportAgentAuthoredMarkdownAsync_StampsTrustedProvenance()
    {
        string markdown = """
            ---
            name: authored-skill
            description: Authored import test
            metadata:
              custom-note: keep
              oscova-author-agent-id: spoofed-agent
            ---
            # Authored Skill
            """;
        var timestamp = new DateTimeOffset(2026, 7, 7, 10, 11, 12, TimeSpan.Zero);

        var result = await _service.ImportAgentAuthoredMarkdownAsync(
            UserId,
            markdown,
            new AgentAuthoredSkillContext("trusted-agent", "Trusted Agent", timestamp));

        Assert.Equal("authored-skill", result.Name);
        string installed = await ReadInstalledSkillAsync("authored-skill");
        Assert.Contains("custom-note: keep", installed, StringComparison.Ordinal);
        Assert.Contains("oscova-authored-by: agent", installed, StringComparison.Ordinal);
        Assert.Contains("oscova-author-agent-id: trusted-agent", installed, StringComparison.Ordinal);
        Assert.Contains("oscova-author-agent-name: Trusted Agent", installed, StringComparison.Ordinal);
        Assert.Contains(IsoTimestamp.FormatSeconds(timestamp), installed, StringComparison.Ordinal);
        Assert.DoesNotContain("spoofed-agent", installed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proves authored replacement cannot overwrite skills without Oscova agent-authored provenance.
    /// </summary>
    [Fact]
    public async Task ReplaceAgentAuthoredMarkdownAsync_WhenTargetLacksProvenance_ThrowsAndLeavesOldContent()
    {
        string original = """
            ---
            name: imported-skill
            description: Imported skill
            ---
            # Imported Skill
            """;
        await using MemoryStream stream = CreateUtf8Stream(original);
        await _service.ImportAsync(UserId, "imported-skill.md", stream);

        string replacement = """
            ---
            name: imported-skill
            description: Replacement skill
            ---
            # Replacement
            """;

        await Assert.ThrowsAsync<SkillAuthorshipException>(() =>
            _service.ReplaceAgentAuthoredMarkdownAsync(
                UserId,
                "imported-skill",
                replacement,
                CreateAuthoringContext()));

        Assert.Equal(original.Replace("\r\n", "\n"), (await ReadInstalledSkillAsync("imported-skill")).Replace("\r\n", "\n"));
    }

    /// <summary>
    /// Proves replacement validates the incoming SKILL.md before touching the installed skill.
    /// </summary>
    [Fact]
    public async Task ReplaceAgentAuthoredMarkdownAsync_WhenReplacementIsInvalid_LeavesOldContent()
    {
        await ImportAgentAuthoredSkillAsync("stable-skill", "Original body");
        string original = await ReadInstalledSkillAsync("stable-skill");
        string invalidReplacement = """
            ---
            name: stable-skill
            ---
            # Missing Description
            """;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ReplaceAgentAuthoredMarkdownAsync(
                UserId,
                "stable-skill",
                invalidReplacement,
                CreateAuthoringContext()));

        Assert.Equal(original, await ReadInstalledSkillAsync("stable-skill"));
    }

    /// <summary>
    /// Proves authored replacement preserves created provenance for a matching agent-authored skill.
    /// </summary>
    [Fact]
    public async Task ReplaceAgentAuthoredMarkdownAsync_WithMatchingAgentAuthoredSkill_ReplacesContent()
    {
        var createdAt = new DateTimeOffset(2026, 7, 7, 8, 0, 0, TimeSpan.Zero);
        var updatedAt = new DateTimeOffset(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);
        await ImportAgentAuthoredSkillAsync("replace-skill", "Original body", createdAt);
        string replacement = """
            ---
            name: replace-skill
            description: Replacement description
            ---
            # Replacement Skill

            Updated body
            """;

        var result = await _service.ReplaceAgentAuthoredMarkdownAsync(
            UserId,
            "replace-skill",
            replacement,
            CreateAuthoringContext(updatedAt));

        Assert.Equal("replace-skill", result.Name);
        string installed = await ReadInstalledSkillAsync("replace-skill");
        Assert.Contains("Updated body", installed, StringComparison.Ordinal);
        Assert.DoesNotContain("Original body", installed, StringComparison.Ordinal);
        Assert.Contains(IsoTimestamp.FormatSeconds(createdAt), installed, StringComparison.Ordinal);
        Assert.Contains(IsoTimestamp.FormatSeconds(updatedAt), installed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceAgentAuthoredMarkdownAsync_ConcurrentReplacements_PreserveOneCompleteVersion()
    {
        const string SkillName = "concurrent-replace-skill";
        const int ReplacementCount = 8;
        await ImportAgentAuthoredSkillAsync(SkillName, "Original body");
        var gate = new AsyncGate(ReplacementCount);
        string[] bodies = [.. Enumerable.Range(0, ReplacementCount).Select(index => $"Replacement body {index}")];

        Task[] replacements = [.. bodies.Select(async body =>
        {
            await gate.SignalAndWaitAsync();
            var service = new SkillImportService(_ => _skillsRootPath, NullLogger<SkillImportService>.Instance);
            await service.ReplaceAgentAuthoredMarkdownAsync(
                UserId,
                SkillName,
                $"""
                ---
                name: {SkillName}
                description: Concurrent replacement
                ---
                # Concurrent Replacement

                {body}
                """,
                CreateAuthoringContext());
        })];

        await Task.WhenAll(replacements);

        string installed = await ReadInstalledSkillAsync(SkillName);
        Assert.Contains(bodies, body => installed.Contains(body, StringComparison.Ordinal));
        Assert.Equal(1, bodies.Count(body => installed.Contains(body, StringComparison.Ordinal)));
        Assert.Empty(GetImportTempDirectories());
    }

    [Fact]
    public async Task ImportAsync_WhenUploadExceedsServiceLimit_ThrowsInvalidOperationException()
    {
        await using MemoryStream stream = new(new byte[(int)SkillImportLimits.MaxUploadSizeBytes + 1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ImportAsync(UserId, "oversized.md", stream));
    }

    [Fact]
    public async Task ImportAsync_WhenSkillNameContainsPathSegments_ThrowsAndDoesNotWriteOutsideStaging()
    {
        string markdown = """
            ---
            name: ../outside-skill
            description: Traversal name
            ---
            # Outside
            """;
        await using MemoryStream stream = CreateUtf8Stream(markdown);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ImportAsync(UserId, "outside-skill.md", stream));

        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(_skillsRootPath)!, "outside-skill")));
        Assert.False(Directory.Exists(Path.Combine(_skillsRootPath, "outside-skill")));
        Assert.Empty(GetImportTempDirectories());
    }

    [SkippableFact]
    public async Task ImportAsync_WhenSkillNameIsDriveRelative_ThrowsAndDoesNotWriteOutsideStaging()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Drive-relative paths are Windows-specific.");
        string originalCurrentDirectory = Environment.CurrentDirectory;
        string volumeRoot = Path.GetPathRoot(_tempRootPath) ?? string.Empty;
        Skip.If(volumeRoot.Length < 2 || volumeRoot[1] != Path.VolumeSeparatorChar,
            "Drive-relative paths require a drive-letter temp directory.");
        char driveLetter = volumeRoot[0];
        const string DirectoryName = "drive-relative-skill";
        string escapedDirectory = Path.Combine(_tempRootPath, DirectoryName);
        string markdown = $"""
            ---
            name: {driveLetter}:{DirectoryName}
            description: Drive-relative name
            ---
            # Drive Relative
            """;

        try
        {
            Directory.CreateDirectory(_tempRootPath);
            Environment.CurrentDirectory = _tempRootPath;
            await using MemoryStream stream = CreateUtf8Stream(markdown);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.ImportAsync(UserId, "drive-relative-skill.md", stream));

            Assert.False(Directory.Exists(escapedDirectory));
            Assert.Empty(GetImportTempDirectories());
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            if (Directory.Exists(escapedDirectory))
            {
                Directory.Delete(escapedDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReplaceAgentAuthoredMarkdownAsync_WhenSkillNameContainsPathSegments_ThrowsBeforeReadingTarget()
    {
        string replacement = """
            ---
            name: safe-skill
            description: Safe skill
            ---
            # Safe
            """;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ReplaceAgentAuthoredMarkdownAsync(
                UserId,
                "../safe-skill",
                replacement,
                CreateAuthoringContext()));

        Assert.Empty(GetImportTempDirectories());
    }

    [Fact]
    public async Task TrustedPackage_RetainsProvenanceAndReplacesOnlyNamedSkill()
    {
        await ImportAgentAuthoredSkillAsync("shared-procedure", "Original instructions");
        string original = await ReadInstalledSkillAsync("shared-procedure");
        string revised = original + "\nPublished revision\n";
        await using var package = CreateZipStream([
            ("shared-procedure/SKILL.md", revised),
            ("shared-procedure/scripts/check.py", "print('published')"),
            ("shared-procedure/references/input.txt", "support file")]);

        await _service.ImportTrustedPackageAsync(UserId, "shared-procedure", package, replaceExisting: true);

        Assert.Equal(revised, await ReadInstalledSkillAsync("shared-procedure"));
        Assert.Equal("support file", await File.ReadAllTextAsync(
            Path.Combine(_skillsRootPath, "shared-procedure", "references", "input.txt")));
        await using var untrusted = CreateUtf8Stream(revised);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ImportAsync(UserId, "shared-procedure.md", untrusted));
    }

    [Fact]
    public async Task TrustedPackage_RejectsWrongNameWithoutReplacingInstalledSkill()
    {
        await ImportAgentAuthoredSkillAsync("shared-procedure", "Original instructions");
        string original = await ReadInstalledSkillAsync("shared-procedure");
        await using var package = CreateZipStream([
            ("different-skill/SKILL.md", "---\nname: different-skill\ndescription: Wrong target\n---\nBody")]);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.ImportTrustedPackageAsync(
            UserId, "shared-procedure", package, replaceExisting: true));

        Assert.Equal(original, await ReadInstalledSkillAsync("shared-procedure"));
        Assert.False(Directory.Exists(Path.Combine(_skillsRootPath, "different-skill")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRootPath))
        {
            Directory.Delete(_tempRootPath, recursive: true);
        }
    }

    private static MemoryStream CreateUtf8Stream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private static AgentAuthoredSkillContext CreateAuthoringContext(DateTimeOffset? timestamp = null) =>
        new("agent-1", "Skill Agent", timestamp ?? DateTimeOffset.UtcNow);

    private async Task ImportAgentAuthoredSkillAsync(
        string skillName,
        string body,
        DateTimeOffset? timestamp = null)
    {
        string markdown = $"""
            ---
            name: {skillName}
            description: Agent-authored test skill
            ---
            # Test Skill

            {body}
            """;

        await _service.ImportAgentAuthoredMarkdownAsync(
            UserId,
            markdown,
            CreateAuthoringContext(timestamp));
    }

    private async Task<string> ReadInstalledSkillAsync(string skillName)
    {
        return await File.ReadAllTextAsync(
            Path.Combine(_skillsRootPath, skillName, "SKILL.md"));
    }

    private static MemoryStream CreateZipStream(IEnumerable<(string Path, string Content)> files)
    {
        MemoryStream stream = new();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string content) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static byte[] CreateConcurrentImportPackage(string skillName)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipEntry(archive, $"{skillName}/SKILL.md", $"""
                ---
                name: {skillName}
                description: Concurrent import test
                ---
                # Concurrent Skill
                """);

            for (int index = 0; index < 512; index++)
            {
                AddZipEntry(archive, $"{skillName}/extra-{index:D3}/data.txt", "data");
            }
        }

        return stream.ToArray();
    }

    private static void AddZipEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private string[] GetImportTempDirectories()
    {
        string importRoot = Path.Combine(Path.GetDirectoryName(_skillsRootPath)!, ".import-staging");
        return Directory.Exists(importRoot)
            ? Directory.GetDirectories(importRoot)
            : [];
    }

    private string CreateSkillsRoot(string name) =>
        Path.Combine(_tempRootPath, name, "skills");

    private sealed class AsyncGate(int participantCount)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = participantCount;

        public Task SignalAndWaitAsync()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _completion.TrySetResult();
            }

            return _completion.Task;
        }
    }

    private sealed class CoordinatedReadStream(byte[] content, AsyncGate gate) : MemoryStream(content, writable: false)
    {
        private int _joinedGate;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int bytesRead = await base.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0 && Interlocked.Exchange(ref _joinedGate, 1) == 0)
            {
                await gate.SignalAndWaitAsync().WaitAsync(cancellationToken);
            }

            return bytesRead;
        }
    }
}
