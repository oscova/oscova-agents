using System.Text.Json;
using Oscova.Agents.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Memory;
using Oscova.Agents.Persistence;
using System.Collections;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Tests.Framework.Memory;

public sealed class AgentMemoryServiceTests : IDisposable
{
    private const long MaxTextSizeBytes = 512 * 1024;
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), $"oscova-agent-memory-{Guid.NewGuid():N}");
    private readonly string _outsideLinkTarget = Path.Combine(Path.GetTempPath(), $"oscova-agent-memory-outside-{Guid.NewGuid():N}");
    private readonly PassThroughContentProtector _protector = new();
    private readonly string _userId = Guid.NewGuid().ToString();
    private static IReadOnlyDictionary<string, object?> Metadata(string title = "Preference") =>
        new Dictionary<string, object?> { ["type"] = "Preference", ["title"] = title, ["description"] = "Durable preference" };
    public void Dispose()
    {
        if (Directory.Exists(_testRoot)) { Directory.Delete(_testRoot, true); }
        if (Directory.Exists(_outsideLinkTarget)) { Directory.Delete(_outsideLinkTarget, true); }
    }

    [Fact]
    public async Task Save_CreatesEncryptedRoutersAndPreservesExtensibleMetadata()
    {
        var store = CreateService().GetStore(_userId);
        using var json = JsonDocument.Parse("{\"opacity\":0.65,\"blur\":true,\"code\":\"0012\"}");
        var metadata = new Dictionary<string, object?>(Metadata("Translucent panels"))
        { ["custom"] = json.RootElement.Clone(), ["tags"] = new[] { "glass" } };
        await store.SaveAsync("design/glass", metadata, "original");
        await store.SaveAsync("design/more/parameters", Metadata("Parameters"), "Tint blue.");
        await store.SaveAsync("design/glass", new Dictionary<string, object?> { ["description"] = "Panel styling" },
            "[Details](more/parameters.md) [Missing](/design/absent.md)");
        var text = await store.ReadAsync("design/glass.md");
        Assert.Contains("opacity: 0.65", text);
        Assert.Contains("blur: true", text);
        Assert.Contains("type: Preference", text);
        var plaintext = _protector.Unprotect(_userId, EncryptedFileContent.ProtectionPurpose,
            await File.ReadAllTextAsync(GetUserPath("memory/design/glass.md.enc")));
        Assert.NotNull(plaintext);
        var (yaml, _) = MarkdownFrontmatter.Split(plaintext);
        var custom = Assert.IsAssignableFrom<IDictionary>(new YamlConverter().DeserializeMapping(yaml)["custom"]);
        Assert.Equal("0012", custom["code"]);
        Assert.Contains("generated:", text);
        Assert.Contains("design/more/parameters.md", text);
        Assert.Contains("design/absent.md (not saved)", text);
        Assert.Contains("okf_version: \"0.2\"", await store.ReadAsync());
        Assert.Contains("/design/index.md", await store.ReadAsync());
        Assert.Contains("/design/glass.md", await store.ReadAsync("design/index.md"));
        Assert.DoesNotContain("okf_version:", await store.ReadAsync("design/index.md"));
        Assert.DoesNotContain("Panel styling", await File.ReadAllTextAsync(GetUserPath("memory/design/index.md.enc")));
        File.Delete(GetUserPath("memory/design/index.md.enc"));
        Assert.Contains("Panel styling", await store.ReadAsync("design/index.md"));
        Assert.Contains("conceptId: design/glass", await store.FindAsync("glass"));
        await store.SaveAsync("design/panel config", Metadata("Panel config"), "space-reference-body");
        Assert.Contains("/design/panel%20config.md", await store.ReadAsync("design/index.md"));
        Assert.Contains("space-reference-body", await store.ReadAsync("/design/panel%20config.md"));
    }

    [Fact]
    public async Task Find_SearchesLargeNestedCorpusBodiesWithoutResultCap()
    {
        var store = CreateService().GetStore(_userId);
        for (var i = 0; i < 2000; i++)
        {
            await WriteUserFileAsync($"memory/group-{i % 20}/concept-{i}.md.enc",
                $"---\ntype: Fact\ntitle: Record {i}\ndescription: shared matching phrase\n---\n" +
                (i == 1999 ? "bodyonlyneedle" : "ordinary body"));
        }
        Assert.Contains("conceptId: group-19/concept-1999", await store.FindAsync("bodyonlyneedle"));
        Assert.Equal(2000, (await store.FindAsync("matching phrase")).Split("conceptId:", StringSplitOptions.None).Length - 1);
        await store.SaveAsync("daily/2026-08-02", new Dictionary<string, object?> { ["type"] = "Daily Memory" }, "bodyonlyneedle");
        Assert.DoesNotContain("daily/2026", await store.FindAsync("bodyonlyneedle", "daily"));
        await Assert.ThrowsAsync<AgentMemoryValidationException>(() => store.FindAsync("?"));
    }

    /// <summary>Returned references work unchanged when linked from another nested concept.</summary>
    [Fact]
    public async Task ReturnedDocumentReference_ResolvesFromNestedSummaryAndReportsInboundLink()
    {
        var store = CreateService().GetStore(_userId);
        var receipt = await store.SaveAsync("design/details", Metadata("Details"), "detail marker");
        var reference = receipt.Split('\n').Single(line => line.StartsWith("document:", StringComparison.Ordinal))["document:".Length..].Trim();
        await store.SaveAsync("design/summary", Metadata("Summary"), $"[Details]({reference})");
        var summary = await store.ReadAsync("design/summary");
        Assert.Contains("/design/details.md", summary);
        Assert.DoesNotContain("(not saved)", summary);
        Assert.Contains("detail marker", await store.ReadAsync(reference));
        Assert.Contains("design/summary", await store.DeleteAsync("design/details"));
    }

    /// <summary>Case aliases follow native file equality without duplicating or merging distinct files.</summary>
    [Fact]
    public async Task CaseAliasSave_KeepsCurrentMetadataForEachPhysicalFile()
    {
        var store = CreateService().GetStore(_userId);
        await store.SaveAsync("Design/Theme", Metadata("Original"), "old marker");
        await store.SaveAsync("design/theme", Metadata("Current"), "new marker");
        var index = await store.ReadAsync();
        if (PathHelper.ArePathsEqual(GetUserPath("memory/Design"), GetUserPath("memory/design")))
        {
            Assert.Equal(1, index!.Split("/index.md", StringSplitOptions.None).Length - 1);
            var nested = await store.ReadAsync("design/index");
            Assert.Equal(1, nested!.Split("- [", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("Original", nested);
            Assert.Contains("Current", nested);
            await store.SaveAsync("design/summary", Metadata("Summary"), "[Theme](/Design/Theme.md)");
            var inbound = (await store.DeleteAsync("design/theme")).Split("Remaining inbound links: ", StringSplitOptions.None)[1];
            Assert.True(PathHelper.ArePathsEqual(GetUserPath("memory/design/summary.md.enc"), GetUserPath("memory/" + inbound + ".md.enc")));
        }
        else
        {
            Assert.Contains("Original", await store.ReadAsync("Design/index"));
            Assert.Contains("Current", await store.ReadAsync("design/index"));
        }
    }

    [Fact]
    public async Task DailyLogs_UseResolvedDateAndRetainFormat()
    {
        var service = CreateService();
        await service.GetStore(_userId).SaveAsync("daily/2026-08-02",
            new Dictionary<string, object?> { ["type"] = "Daily Memory" }, "today");
        Assert.Equal(new DateOnly(2026, 8, 2), (await service.ReadDailyLogAsync(_userId)).Date);
        Assert.Contains("today", (await service.ReadDailyLogAsync(_userId)).Content);
        Assert.Equal(["2026-08-02"], await service.ListDailyLogsDatesAsync(_userId));
        await service.AppendDailyLogAsync(_userId, "temporary plan", TimeZoneInfo.Utc);
        Assert.Contains("type: Daily Memory", await service.ReadDailyLogForDateAsync(_userId, DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Fact]
    public async Task Delete_BacksUpTargetAndReportsLinksWithoutDeletingDetails()
    {
        var backups = CreateBackupService();
        var store = CreateService(backups).GetStore(_userId);
        await store.SaveAsync("summary", Metadata(), "[Linked detail](details/config.md)");
        await store.SaveAsync("details/config", Metadata(), "retained");
        await store.SaveAsync("reference", Metadata(), "[Summary](summary.md)");
        Assert.Contains("reference", await store.DeleteAsync("summary"));
        Assert.False(File.Exists(GetUserPath("memory/summary.md.enc")));
        Assert.Contains("retained", await store.ReadAsync("details/config"));
        Assert.Contains("[Summary](summary.md)", await store.ReadAsync("reference"));
        Assert.DoesNotContain("/summary.md", await store.ReadAsync());
        Assert.Single(await backups.ListBackupsAsync(_userId, "memory/summary.md.enc"));
    }

    [Fact]
    public async Task ConcurrentSaves_FromSeparateStoresRetainBothIndexEntries()
    {
        var backups = new CoordinatedBackupService();
        var service = CreateService(backups);
        var first = service.GetStore(_userId).SaveAsync("first", Metadata(), "one");
        Assert.Same(backups.FirstCaptureStarted, await Task.WhenAny(backups.FirstCaptureStarted, first));
        var second = service.GetStore(_userId).SaveAsync("second", Metadata(), "two");
        backups.ReleaseFirstCapture();
        await Task.WhenAll(first, second);
        var index = await service.ReadLongTermMemoryAsync(_userId);
        Assert.Contains("/first.md", index);
        Assert.Contains("/second.md", index);
    }

    [Fact]
    public async Task InvalidMetadataAndOversizedUtf8_DoNotMutateExistingConceptOrIndex()
    {
        var store = CreateService().GetStore(_userId);
        await store.SaveAsync("existing", Metadata(), "original");
        var index = await store.ReadAsync();
        await Assert.ThrowsAsync<AgentMemoryValidationException>(() =>
            store.SaveAsync("existing", new Dictionary<string, object?> { ["type"] = "" }, "replacement"));
        await Assert.ThrowsAsync<AgentMemoryValidationException>(() =>
            store.SaveAsync("oversized", Metadata(), new string('é', (int)MaxTextSizeBytes)));
        await Assert.ThrowsAsync<AgentMemoryValidationException>(() =>
            store.SaveAsync("untrusted", new Dictionary<string, object?> { ["type"] = "Fact", ["verified"] = true }, "claim"));
        Assert.Contains("original", await store.ReadAsync("existing"));
        Assert.Equal(index, await store.ReadAsync());
        Assert.False(File.Exists(GetUserPath("memory/oversized.md.enc")));
    }

    [SkippableFact]
    public void Save_WhenMemoryDirectoryIsLinked_RejectsEscape()
    {
        var memoryPath = GetUserPath("memory");
        Directory.CreateDirectory(GetUserPath(""));
        Directory.CreateDirectory(_outsideLinkTarget);
        try
        {
            try { Directory.CreateSymbolicLink(memoryPath, _outsideLinkTarget); }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            { Skip.If(true, $"Symbolic link creation is unavailable: {ex.Message}"); }
            Assert.Throws<UnauthorizedAccessException>(() => CreateService().GetStore(_userId));
            Assert.False(File.Exists(Path.Combine(_outsideLinkTarget, "index.md.enc")));
        }
        finally { if (Directory.Exists(memoryPath)) { Directory.Delete(memoryPath); } }
    }
    /// <summary>Proves a scoped delete keeps daily memory and foreign files, captures each deleted file, and overwrites its one sibling snapshot.</summary>
    [Fact]
    public async Task DeleteAll_ReplacesSnapshotAndKeepsDailyAndForeignFiles()
    {
        var backups = new RecordingBackupService();
        var service = CreateService(backups);
        var store = service.GetStore(_userId);
        await store.SaveAsync("first", Metadata("First"), "one");
        await store.SaveAsync("topic/second", Metadata("Second"), "two");
        await store.SaveAsync("daily/2026-08-02", new Dictionary<string, object?> { ["type"] = "Daily Memory" }, "Kept daily entry.");
        await File.WriteAllTextAsync(GetUserPath("memory/vectors.db"), "foreign");

        backups.Paths.Clear();
        Assert.Equal("Deleted 2 concepts.", await store.DeleteAllAsync(AgentMemoryService.DailyDirectoryName, snapshot: true).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Single(backups.Paths, path => path == "memory/first.md.enc");
        Assert.Single(backups.Paths, path => path == "memory/topic/second.md.enc");

        await store.SaveAsync("third", Metadata("Third"), "three");
        Assert.Equal("Deleted 1 concepts.", await store.DeleteAllAsync(AgentMemoryService.DailyDirectoryName, snapshot: true).WaitAsync(TimeSpan.FromSeconds(30)));

        var snapshot = GetUserPath("memory.bak");
        var copied = Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(snapshot, file).Replace('\\', '/')).ToArray();
        Assert.Contains("third.md.enc", copied);
        Assert.DoesNotContain(copied, file => file.StartsWith("first", StringComparison.Ordinal) || file.StartsWith("topic/", StringComparison.Ordinal) ||
            file.StartsWith("daily/", StringComparison.Ordinal) || file == "vectors.db");
        Assert.Equal("foreign", await File.ReadAllTextAsync(GetUserPath("memory/vectors.db")));
        Assert.Contains("Kept daily entry.", (await service.ReadDailyLogAsync(_userId)).Content);
        Assert.False(Directory.Exists(GetUserPath("memory/topic")));
        Assert.DoesNotContain(store.ListConceptIds(string.Empty), id => !id.StartsWith("daily/", StringComparison.Ordinal));
        Assert.DoesNotContain("third", await store.ReadAsync());
    }

    private AgentMemoryService CreateService(
        IUserFileBackupService? backups = null,
        Func<string, string>? rootResolver = null,
        Func<string, Task<DateOnly>>? dateResolver = null)
    {
        return new AgentMemoryService(
            rootResolver ?? (userId => Path.Combine(_testRoot, userId)),
            "memory",
            MaxTextSizeBytes,
            dateResolver ?? (_ => Task.FromResult(new DateOnly(2026, 8, 2))),
            backups ?? new NoopUserFileBackupService(),
            _protector);
    }

    private UserFileBackupService CreateBackupService()
    {
        return new UserFileBackupService(
            userId => Path.Combine(_testRoot, userId),
            path => string.Equals(Path.GetExtension(path), ".enc", StringComparison.OrdinalIgnoreCase),
            MaxTextSizeBytes,
            NullLogger<UserFileBackupService>.Instance);
    }

    private string GetUserPath(string relativePath)
    {
        return Path.Combine(_testRoot, _userId, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private Task WriteUserFileAsync(string relativePath, string content)
    {
        return WriteFileAsync(GetUserPath(relativePath), content);
    }

    private async Task WriteFileAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, _protector.Protect(_userId, EncryptedFileContent.ProtectionPurpose, content));
    }

    private sealed class RecordingBackupService : IUserFileBackupService
    {
        public List<string> Paths { get; } = [];

        public Task<UserFileBackupCaptureResult> CaptureBeforeMutationAsync(string userId, string relativePath, CancellationToken ct = default)
        {
            Paths.Add(relativePath);
            return Task.FromResult(new UserFileBackupCaptureResult(UserFileBackupCaptureStatus.SkippedMissing, null));
        }

        public Task<IReadOnlyList<UserFileBackupEntry>> ListBackupsAsync(string userId, string relativePath, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserFileBackupEntry>>([]);

        public Task<UserFileBackupContent> ReadBackupAsync(string userId, string relativePath, string backupId, CancellationToken ct = default) =>
            throw new FileNotFoundException();

        public Task<UserFileBackupEntry> RestoreBackupAsync(string userId, string relativePath, string backupId, CancellationToken ct = default) =>
            throw new FileNotFoundException();
    }

    private sealed class CoordinatedBackupService : IUserFileBackupService
    {
        private readonly TaskCompletionSource _firstCaptureStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _captureCount;

        public Task FirstCaptureStarted => _firstCaptureStarted.Task;

        public void ReleaseFirstCapture() => _releaseFirstCapture.SetResult();

        public async Task<UserFileBackupCaptureResult> CaptureBeforeMutationAsync(
            string userId,
            string relativePath,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _captureCount) == 1)
            {
                _firstCaptureStarted.SetResult();
                await _releaseFirstCapture.Task.WaitAsync(ct);
            }

            return new UserFileBackupCaptureResult(UserFileBackupCaptureStatus.SkippedMissing, null);
        }

        public Task<IReadOnlyList<UserFileBackupEntry>> ListBackupsAsync(
            string userId,
            string relativePath,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UserFileBackupEntry>>([]);

        public Task<UserFileBackupContent> ReadBackupAsync(
            string userId,
            string relativePath,
            string backupId,
            CancellationToken ct = default) => throw new FileNotFoundException();

        public Task<UserFileBackupEntry> RestoreBackupAsync(
            string userId,
            string relativePath,
            string backupId,
            CancellationToken ct = default) => throw new FileNotFoundException();
    }
}
