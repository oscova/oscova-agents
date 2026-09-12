using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Memory;

namespace Oscova.Agents.Tests.Framework.Memory;

public sealed class AgentMemoryServiceTests : IDisposable
{
    private const long MaxTextSizeBytes = 512 * 1024;
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"oscova-agent-memory-{Guid.NewGuid():N}");
    private readonly string _outsideLinkTarget = Path.Combine(
        Path.GetTempPath(),
        $"oscova-agent-memory-outside-{Guid.NewGuid():N}");
    private readonly string _userId = Guid.NewGuid().ToString();

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        if (Directory.Exists(_outsideLinkTarget))
        {
            Directory.Delete(_outsideLinkTarget, recursive: true);
        }
    }

    [Fact]
    public async Task ReadDailyLogAsync_UsesDateResolver_ForRelativeDailyLogDate()
    {
        var userToday = new DateOnly(2026, 8, 2);
        var userYesterday = userToday.AddDays(-1);
        var service = CreateService(dateResolver: _ => Task.FromResult(userToday));
        await WriteUserFileAsync($"memory/daily/{userToday:yyyy-MM-dd}.md", "user today log");
        await WriteUserFileAsync($"memory/daily/{userYesterday:yyyy-MM-dd}.md", "user yesterday log");

        var today = await service.ReadDailyLogAsync(_userId);
        var yesterday = await service.ReadDailyLogAsync(_userId, daysAgo: 1);

        Assert.Equal(userToday, today.Date);
        Assert.Equal("user today log", today.Content);
        Assert.Equal(userYesterday, yesterday.Date);
        Assert.Equal("user yesterday log", yesterday.Content);
    }

    [Fact]
    public async Task AppendLongTermMemoryAsync_WithTopic_UpdatesIndexAndTopicFile()
    {
        var service = CreateService();

        await service.AppendLongTermMemoryAsync(_userId, "The user prefers small focused patches.", "repo-work");
        await service.AppendLongTermMemoryAsync(_userId, "The user wants verification evidence.", "verification");

        var index = await service.ReadLongTermMemoryAsync(_userId);
        var topic = await service.ReadTopicMemoryAsync(_userId, "repo-work");

        Assert.Contains("- [repo-work](repo-work.md)", index);
        Assert.Contains("- [verification](verification.md)", index);
        Assert.Equal(1, CountOccurrences(index ?? string.Empty, "## Topics"));
        Assert.Contains("The user prefers small focused patches.", topic);
    }

    [Fact]
    public async Task ReadLongTermMemoryAsync_WhenIndexExceedsEditorLimit_ReturnsRuntimeContent()
    {
        var memoryDirectory = GetUserPath("memory");
        Directory.CreateDirectory(memoryDirectory);
        var oversizedContent = new string('m', checked((int)MaxTextSizeBytes + 1));
        await File.WriteAllTextAsync(Path.Combine(memoryDirectory, "index.md"), oversizedContent);
        var service = CreateService();

        var content = await service.ReadLongTermMemoryAsync(_userId);

        Assert.NotNull(content);
        Assert.Equal(checked((int)MaxTextSizeBytes), content.Length);
        Assert.StartsWith(content, oversizedContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendLongTermMemoryAsync_BacksUpExistingIndexBeforeAppend()
    {
        var backupService = CreateBackupService();
        var service = CreateService(backupService);
        await WriteUserFileAsync("memory/index.md", "before");

        await service.AppendLongTermMemoryAsync(_userId, "after");

        var backups = await backupService.ListBackupsAsync(_userId, "memory/index.md");
        var backup = await backupService.ReadBackupAsync(_userId, "memory/index.md", Assert.Single(backups).Id);
        Assert.Equal("before", backup.Content);
    }

    [Fact]
    public async Task ForgetLongTermMemoryAsync_BacksUpOnlyWhenFileChanges()
    {
        var backupService = CreateBackupService();
        var service = CreateService(backupService);
        await WriteUserFileAsync("memory/index.md", "keep\nforget me\n");

        var changed = await service.ForgetLongTermMemoryAsync(_userId, "forget me");
        var unchanged = await service.ForgetLongTermMemoryAsync(_userId, "missing");

        var backups = await backupService.ListBackupsAsync(_userId, "memory/index.md");
        var backup = await backupService.ReadBackupAsync(_userId, "memory/index.md", Assert.Single(backups).Id);
        Assert.Equal(1, changed.FilesUpdated);
        Assert.Equal(0, unchanged.FilesUpdated);
        Assert.Equal("keep\nforget me\n", backup.Content);
    }

    [Fact]
    public async Task ConcurrentForgetAndAppend_PreserveBothSuccessfulMutations()
    {
        var backupService = new CoordinatedBackupService();
        var service = CreateService(backupService);
        await WriteUserFileAsync("memory/index.md", "keep\nforget me\n");

        var forgetTask = service.ForgetLongTermMemoryAsync(_userId, "forget me");
        await backupService.FirstCaptureStarted;
        var appendTask = service.AppendLongTermMemoryAsync(_userId, "new fact");
        backupService.ReleaseFirstCapture();
        await Task.WhenAll(forgetTask, appendTask);

        var content = await service.ReadLongTermMemoryAsync(_userId);
        Assert.DoesNotContain("forget me", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new fact", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteLongTermMemoryAsync_WhenContentExceedsLimit_DoesNotCreateFile()
    {
        var service = CreateService();
        var oversizedContent = new string('m', checked((int)MaxTextSizeBytes + 1));

        await Assert.ThrowsAsync<AgentMemoryValidationException>(() =>
            service.WriteLongTermMemoryAsync(_userId, oversizedContent));

        Assert.False(File.Exists(GetUserPath("memory/index.md")));
    }

    [Fact]
    public async Task ReadLongTermMemoryAsync_WhenUserRootChanges_UsesCurrentRoot()
    {
        var firstRoot = Path.Combine(_testRoot, "first");
        var secondRoot = Path.Combine(_testRoot, "second");
        var currentRoot = firstRoot;
        var service = CreateService(rootResolver: userId => Path.Combine(currentRoot, userId));
        await WriteFileAsync(Path.Combine(firstRoot, _userId, "memory", "index.md"), "first root");
        await WriteFileAsync(Path.Combine(secondRoot, _userId, "memory", "index.md"), "second root");

        Assert.Equal("first root", await service.ReadLongTermMemoryAsync(_userId));
        currentRoot = secondRoot;

        Assert.Equal("second root", await service.ReadLongTermMemoryAsync(_userId));
    }

    [SkippableFact]
    public async Task WriteLongTermMemoryAsync_WhenMemoryDirectoryIsLinked_RejectsEscape()
    {
        var userRoot = GetUserPath(string.Empty);
        var memoryPath = Path.Combine(userRoot, "memory");
        Directory.CreateDirectory(userRoot);
        Directory.CreateDirectory(_outsideLinkTarget);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(memoryPath, _outsideLinkTarget);
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            {
                Skip.If(true, $"Symbolic link creation is unavailable on this runner: {exception.Message}");
            }

            var service = CreateService();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                service.WriteLongTermMemoryAsync(_userId, "must stay contained"));
            Assert.False(File.Exists(Path.Combine(_outsideLinkTarget, "index.md")));
        }
        finally
        {
            if (Directory.Exists(memoryPath))
            {
                Directory.Delete(memoryPath);
            }
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private AgentMemoryService CreateService(
        IUserFileBackupService? backups = null,
        Func<string, string>? rootResolver = null,
        Func<string, Task<DateOnly>>? dateResolver = null)
    {
        return new AgentMemoryService(
            rootResolver ?? (userId => Path.Combine(_testRoot, userId)),
            MaxTextSizeBytes,
            dateResolver ?? (_ => Task.FromResult(new DateOnly(2026, 8, 2))),
            backups ?? new NoopUserFileBackupService());
    }

    private UserFileBackupService CreateBackupService()
    {
        return new UserFileBackupService(
            userId => Path.Combine(_testRoot, userId),
            path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase),
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

    private static async Task WriteFileAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
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
