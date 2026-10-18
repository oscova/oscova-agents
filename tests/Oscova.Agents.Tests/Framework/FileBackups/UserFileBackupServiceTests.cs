using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.FileBackups;

namespace Oscova.Agents.Tests.Framework.FileBackups;

public sealed class UserFileBackupServiceTests : IDisposable
{
    private const long MaxFileSizeBytes = 512 * 1024;

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"oscova-file-backups-{Guid.NewGuid():N}");
    private readonly TestTimeProvider _timeProvider = new(new DateTimeOffset(2026, 7, 7, 16, 42, 10, 123, TimeSpan.Zero));
    private readonly UserFileBackupService _service;
    private readonly string _userId = Guid.NewGuid().ToString();
    private readonly string _userDir;

    public UserFileBackupServiceTests()
    {
        _userDir = Path.Combine(_testRoot, _userId);
        Directory.CreateDirectory(_userDir);
        _service = new UserFileBackupService(
            userId => Path.Combine(_testRoot, userId),
            path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase),
            MaxFileSizeBytes,
            NullLogger<UserFileBackupService>.Instance,
            _timeProvider);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_ExistingTextFile_WritesTimestampedBackup()
    {
        await WriteUserFileTextAsync("memory/index.md", "previous");

        var result = await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");
        var backup = await _service.ReadBackupAsync(_userId, "memory/index.md", result.Entry!.Id);

        Assert.Equal(UserFileBackupCaptureStatus.Captured, result.Status);
        Assert.Equal("previous", backup.Content);
        Assert.Equal("memory/index.md", backup.Entry.RelativePath);
        Assert.True(File.Exists(GetBackupFilePath("memory/index.md", backup.Entry.Id)));
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_MissingFile_Skips()
    {
        var result = await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");

        Assert.Equal(UserFileBackupCaptureStatus.SkippedMissing, result.Status);
        Assert.Null(result.Entry);
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_WhenFilePolicyRejectsPath_Skips()
    {
        await WriteUserFileTextAsync("memory/index.txt", "unsupported");

        var result = await _service.CaptureBeforeMutationAsync(_userId, "memory/index.txt");

        Assert.Equal(UserFileBackupCaptureStatus.SkippedUnsupportedExtension, result.Status);
        Assert.Null(result.Entry);
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_OversizedFile_Skips()
    {
        var path = Path.Combine(_userDir, "memory", "large.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[MaxFileSizeBytes + 1]);

        var result = await _service.CaptureBeforeMutationAsync(_userId, "memory/large.md");

        Assert.Equal(UserFileBackupCaptureStatus.SkippedOversized, result.Status);
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_DuplicateLatestContent_Skips()
    {
        await WriteUserFileTextAsync("memory/index.md", "same");
        await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");

        var result = await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");
        var backups = await _service.ListBackupsAsync(_userId, "memory/index.md");

        Assert.Equal(UserFileBackupCaptureStatus.SkippedDuplicate, result.Status);
        Assert.Single(backups);
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_ConcurrentIdenticalCaptures_WriteOneBackup()
    {
        await WriteUserFileTextAsync("memory/index.md", "same");
        using var writeBarrier = new Barrier(2);
        var service = new UserFileBackupService(
            userId => Path.Combine(_testRoot, userId),
            path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase),
            MaxFileSizeBytes,
            NullLogger<UserFileBackupService>.Instance,
            new BarrierTimeProvider(_timeProvider.GetUtcNow(), writeBarrier));

        var results = await Task.WhenAll(
            Task.Run(() => service.CaptureBeforeMutationAsync(_userId, "memory/index.md")),
            Task.Run(() => service.CaptureBeforeMutationAsync(_userId, "memory/index.md")));

        Assert.Single(results, result => result.Status == UserFileBackupCaptureStatus.Captured);
        Assert.Single(results, result => result.Status == UserFileBackupCaptureStatus.SkippedDuplicate);
        Assert.Single(await service.ListBackupsAsync(_userId, "memory/index.md"));
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_PrunesToThirtyVersionsPerFile()
    {
        for (var index = 0; index < UserFileBackupLimits.MaxVersionsPerFile + 5; index++)
        {
            await WriteUserFileTextAsync("memory/index.md", $"version {index}");
            await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");
            _timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        }

        var backups = await _service.ListBackupsAsync(_userId, "memory/index.md");
        var oldest = await _service.ReadBackupAsync(_userId, "memory/index.md", backups[^1].Id);

        Assert.Equal(UserFileBackupLimits.MaxVersionsPerFile, backups.Count);
        Assert.Equal("version 5", oldest.Content);
    }

    [Fact]
    public async Task RestoreBackupAsync_WritesBackupContentAndSnapshotsCurrentContentFirst()
    {
        await WriteUserFileTextAsync("memory/index.md", "old");
        var first = await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");
        await WriteUserFileTextAsync("memory/index.md", "current");

        await _service.RestoreBackupAsync(_userId, "memory/index.md", first.Entry!.Id);

        var restored = await ReadUserFileTextAsync("memory/index.md");
        var backups = await _service.ListBackupsAsync(_userId, "memory/index.md");
        var currentSnapshot = await _service.ReadBackupAsync(_userId, "memory/index.md", backups[0].Id);
        Assert.Equal("old", restored);
        Assert.Equal("current", currentSnapshot.Content);
    }

    [Fact]
    public async Task HistoryPath_IsSkippedForCaptureAndRejectedForListing()
    {
        var result = await _service.CaptureBeforeMutationAsync(_userId, "file-history/memory/index.md");

        Assert.Equal(UserFileBackupCaptureStatus.SkippedHistoryPath, result.Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.ListBackupsAsync(_userId, "file-history/memory/index.md"));
    }

    [Fact]
    public async Task ListBackupsAsync_UsesExactOriginalFileNamePattern()
    {
        var validId = _timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var historyDir = Path.Combine(_userDir, "file-history", "memory");
        Directory.CreateDirectory(historyDir);
        await File.WriteAllTextAsync(Path.Combine(historyDir, $"index.md.{validId}.bak"), "valid");
        await File.WriteAllTextAsync(Path.Combine(historyDir, "index.md.not-a-date.bak"), "malformed");
        await File.WriteAllTextAsync(Path.Combine(historyDir, $"other.md.{validId}.bak"), "wrong file");
        await File.WriteAllTextAsync(Path.Combine(historyDir, $"index.md.{validId}.bak.extra"), "wrong suffix");

        var backups = await _service.ListBackupsAsync(_userId, "memory/index.md");

        var backup = Assert.Single(backups);
        Assert.Equal(validId, backup.Id);
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_CollisionTimestamp_WritesSuffixedBackup()
    {
        await WriteUserFileTextAsync("memory/index.md", "first");
        var first = await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");
        await WriteUserFileTextAsync("memory/index.md", "second");

        var second = await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");

        Assert.Equal(first.Entry!.Id + "-001", second.Entry!.Id);
        Assert.Equal("first", await File.ReadAllTextAsync(GetBackupFilePath("memory/index.md", first.Entry.Id)));
        Assert.Equal("second", await File.ReadAllTextAsync(GetBackupFilePath("memory/index.md", second.Entry.Id)));
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_WhenUserRootChanges_UsesCurrentRoot()
    {
        var firstRoot = Path.Combine(_testRoot, "first", _userId);
        var secondRoot = Path.Combine(_testRoot, "second", _userId);
        var currentRoot = firstRoot;
        var service = new UserFileBackupService(
            _ => currentRoot,
            path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase),
            MaxFileSizeBytes,
            NullLogger<UserFileBackupService>.Instance,
            _timeProvider);

        await WriteUserFileTextAsync("memory/index.md", "first", firstRoot);
        await service.CaptureBeforeMutationAsync(_userId, "memory/index.md");
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        currentRoot = secondRoot;
        await WriteUserFileTextAsync("memory/index.md", "second", secondRoot);

        var second = await service.CaptureBeforeMutationAsync(_userId, "memory/index.md");
        var secondFileName = $"index.md.{second.Entry!.Id}.bak";

        Assert.True(File.Exists(Path.Combine(secondRoot, "file-history", "memory", secondFileName)));
        Assert.False(File.Exists(Path.Combine(firstRoot, "file-history", "memory", secondFileName)));
    }

    [Fact]
    public async Task CaptureBeforeMutationAsync_MalformedExistingHistoryFile_StillCaptures()
    {
        var historyDir = Path.Combine(_userDir, "file-history", "memory");
        Directory.CreateDirectory(historyDir);
        await File.WriteAllTextAsync(Path.Combine(historyDir, "index.md.not-a-date.bak"), "ignored");
        await WriteUserFileTextAsync("memory/index.md", "current");

        var result = await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");

        Assert.Equal(UserFileBackupCaptureStatus.Captured, result.Status);
        Assert.Single(await _service.ListBackupsAsync(_userId, "memory/index.md"));
    }

    [Fact]
    public async Task ListBackupsAsync_WhenPathEscapesUserRoot_Throws()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.ListBackupsAsync(_userId, "../outside.md"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupAccess_WhenBackupIsAnOutsideSymlink_Throws(bool restore)
    {
        await WriteUserFileTextAsync("memory/index.md", "current");
        var capture = await _service.CaptureBeforeMutationAsync(_userId, "memory/index.md");
        string backupPath = GetBackupFilePath("memory/index.md", capture.Entry!.Id);
        string outsidePath = Path.Combine(_testRoot, "outside.md");
        await File.WriteAllTextAsync(outsidePath, "outside secret");
        File.Delete(backupPath);
        File.CreateSymbolicLink(backupPath, outsidePath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            if (restore)
            {
                await _service.RestoreBackupAsync(_userId, "memory/index.md", capture.Entry.Id);
            }
            else
            {
                await _service.ReadBackupAsync(_userId, "memory/index.md", capture.Entry.Id);
            }
        });
        Assert.Equal("current", await ReadUserFileTextAsync("memory/index.md"));
        Assert.Equal("outside secret", await File.ReadAllTextAsync(outsidePath));
    }

    private async Task WriteUserFileTextAsync(string relativePath, string content, string? userRoot = null)
    {
        var path = Path.Combine(userRoot ?? _userDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task<string> ReadUserFileTextAsync(string relativePath)
    {
        var path = Path.Combine(_userDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return await File.ReadAllTextAsync(path);
    }

    private string GetBackupFilePath(string relativePath, string backupId)
    {
        var parent = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        var historyDir = string.IsNullOrWhiteSpace(parent)
            ? Path.Combine(_userDir, "file-history")
            : Path.Combine(_userDir, "file-history", parent);
        return Path.Combine(historyDir, $"{Path.GetFileName(relativePath)}.{backupId}.bak");
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan span)
        {
            _utcNow += span;
        }
    }

    private sealed class BarrierTimeProvider(DateTimeOffset utcNow, Barrier barrier) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            barrier.SignalAndWait();
            return utcNow;
        }
    }
}
