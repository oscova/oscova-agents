using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;

namespace Oscova.Agents.FileBackups;

/// <summary>Stores bounded restore points beneath caller-selected user roots.</summary>
public sealed class UserFileBackupService : IUserFileBackupService
{
    private const string BackupExtension = ".bak";
    private const string HistoryDirectory = "file-history";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private const string BackupIdPattern = @"^\d{8}T\d{9}Z(?:-\d{3})?$";

    private static readonly Regex BackupIdRegex = new(
        BackupIdPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly Func<string, string> _userRootResolver;
    private readonly Func<string, bool> _isSupportedFile;
    private readonly long _maxFileSizeBytes;
    private readonly ILogger<UserFileBackupService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a backup service with caller-owned root, file, and size policies.</summary>
    public UserFileBackupService(
        Func<string, string> userRootResolver,
        Func<string, bool> isSupportedFile,
        long maxFileSizeBytes,
        ILogger<UserFileBackupService> logger)
        : this(userRootResolver, isSupportedFile, maxFileSizeBytes, logger, TimeProvider.System)
    {
    }

    internal UserFileBackupService(
        Func<string, string> userRootResolver,
        Func<string, bool> isSupportedFile,
        long maxFileSizeBytes,
        ILogger<UserFileBackupService> logger,
        TimeProvider timeProvider)
    {
        _userRootResolver = userRootResolver ?? throw new ArgumentNullException(nameof(userRootResolver));
        _isSupportedFile = isSupportedFile ?? throw new ArgumentNullException(nameof(isSupportedFile));
        _maxFileSizeBytes = maxFileSizeBytes;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<UserFileBackupCaptureResult> CaptureBeforeMutationAsync(
        string userId,
        string relativePath,
        CancellationToken ct = default)
    {
        var target = ResolveUserFilePath(userId, relativePath);
        if (IsHistoryPath(target.RelativePath))
        {
            return new(UserFileBackupCaptureStatus.SkippedHistoryPath, null);
        }

        if (Directory.Exists(target.FullPath))
        {
            return new(UserFileBackupCaptureStatus.SkippedDirectory, null);
        }

        if (!File.Exists(target.FullPath))
        {
            return new(UserFileBackupCaptureStatus.SkippedMissing, null);
        }

        if (!_isSupportedFile(target.RelativePath))
        {
            return new(UserFileBackupCaptureStatus.SkippedUnsupportedExtension, null);
        }

        var info = new FileInfo(target.FullPath);
        if (info.Length > _maxFileSizeBytes)
        {
            return new(UserFileBackupCaptureStatus.SkippedOversized, null);
        }

        var currentContent = await File.ReadAllTextAsync(target.FullPath, ct).ConfigureAwait(false);
        var latest = (await ListBackupsAsync(userId, target.RelativePath, ct).ConfigureAwait(false)).FirstOrDefault();
        if (latest is not null)
        {
            try
            {
                var latestContent = await ReadBackupAsync(userId, target.RelativePath, latest.Id, ct).ConfigureAwait(false);
                if (string.Equals(latestContent.Content, currentContent, StringComparison.Ordinal))
                {
                    return new(UserFileBackupCaptureStatus.SkippedDuplicate, null);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(
                    ex,
                    "Latest file backup could not be read before capturing a new backup for {RelativePath}",
                    target.RelativePath);
            }
        }

        var entry = await WriteBackupFileAsync(userId, target.RelativePath, currentContent, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return new(UserFileBackupCaptureStatus.SkippedDuplicate, null);
        }

        await PruneAsync(userId, target.RelativePath, ct).ConfigureAwait(false);
        return new(UserFileBackupCaptureStatus.Captured, entry);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UserFileBackupEntry>> ListBackupsAsync(
        string userId,
        string relativePath,
        CancellationToken ct = default)
    {
        var target = ResolveUserFilePath(userId, relativePath);
        if (IsHistoryPath(target.RelativePath))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        if (!_isSupportedFile(target.RelativePath))
        {
            throw new InvalidOperationException($"File type '{Path.GetExtension(target.RelativePath)}' cannot be backed up.");
        }

        var historyDirectory = GetHistoryDirectory(target.UserRoot, target.RelativePath);
        EnsureHistoryDirectorySafe(target.UserRoot, historyDirectory);
        if (!Directory.Exists(historyDirectory))
        {
            return Task.FromResult<IReadOnlyList<UserFileBackupEntry>>([]);
        }

        var prefix = Path.GetFileName(target.RelativePath) + ".";
        var entries = new List<UserFileBackupEntry>();
        foreach (var backupPath in Directory.EnumerateFiles(historyDirectory))
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(backupPath);
            if (!TryParseBackupFileName(fileName, prefix, out var backupId, out var createdUtc))
            {
                continue;
            }

            entries.Add(new UserFileBackupEntry(
                backupId,
                target.RelativePath,
                createdUtc,
                new FileInfo(GetBackupPath(target.UserRoot, target.RelativePath, backupId)).Length));
        }

        entries.Sort((left, right) =>
        {
            var createdComparison = right.CreatedUtc.CompareTo(left.CreatedUtc);
            return createdComparison != 0
                ? createdComparison
                : string.CompareOrdinal(right.Id, left.Id);
        });

        return Task.FromResult<IReadOnlyList<UserFileBackupEntry>>(entries);
    }

    /// <inheritdoc />
    public async Task<UserFileBackupContent> ReadBackupAsync(
        string userId,
        string relativePath,
        string backupId,
        CancellationToken ct = default)
    {
        var target = ResolveUserFilePath(userId, relativePath);
        if (!TryParseBackupId(backupId, out _))
        {
            throw new InvalidOperationException("Backup id is invalid.");
        }

        var entry = (await ListBackupsAsync(userId, target.RelativePath, ct).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.Id, backupId, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new FileNotFoundException("Backup not found.");
        }

        var backupPath = GetBackupPath(target.UserRoot, target.RelativePath, backupId);
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("Backup not found.");
        }

        var content = await File.ReadAllTextAsync(backupPath, ct).ConfigureAwait(false);
        return new UserFileBackupContent(entry, content);
    }

    /// <inheritdoc />
    public async Task<UserFileBackupEntry> RestoreBackupAsync(
        string userId,
        string relativePath,
        string backupId,
        CancellationToken ct = default)
    {
        var backup = await ReadBackupAsync(userId, relativePath, backupId, ct).ConfigureAwait(false);

        await CaptureBeforeMutationAsync(userId, backup.Entry.RelativePath, ct).ConfigureAwait(false);
        await WriteRestoredContentAsync(userId, backup.Entry.RelativePath, backup.Content).ConfigureAwait(false);

        return backup.Entry;
    }

    private async Task WriteRestoredContentAsync(string userId, string relativePath, string content)
    {
        var target = ResolveUserFilePath(userId, relativePath);
        if (Encoding.UTF8.GetByteCount(content) > _maxFileSizeBytes)
        {
            throw new InvalidOperationException("Backup content exceeds the configured file size limit.");
        }

        await FileUtil.WriteAllTextAtomicAsync(target.FullPath, content).ConfigureAwait(false);
    }

    private async Task<UserFileBackupEntry?> WriteBackupFileAsync(
        string userId,
        string relativePath,
        string content,
        CancellationToken ct)
    {
        var target = ResolveUserFilePath(userId, relativePath);
        var timestamp = _timeProvider.GetUtcNow().ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var historyDirectory = GetHistoryDirectory(target.UserRoot, target.RelativePath);
        EnsureHistoryDirectorySafe(target.UserRoot, historyDirectory);
        Directory.CreateDirectory(historyDirectory);

        for (var suffix = 0; suffix <= 999; suffix++)
        {
            var backupId = suffix == 0
                ? timestamp
                : $"{timestamp}-{suffix:000}";
            var backupPath = GetBackupPath(target.UserRoot, target.RelativePath, backupId);
            var tempPath = FileUtil.CreateSiblingTempPath(backupPath);

            try
            {
                await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);
                File.Move(tempPath, backupPath, overwrite: false);
                return new UserFileBackupEntry(
                    backupId,
                    target.RelativePath,
                    ParseBackupIdUtc(backupId),
                    new FileInfo(backupPath).Length);
            }
            catch (IOException) when (File.Exists(backupPath))
            {
                TryDeleteFile(tempPath);
                var existingContent = await File.ReadAllTextAsync(backupPath, ct).ConfigureAwait(false);
                if (string.Equals(existingContent, content, StringComparison.Ordinal))
                {
                    return null;
                }
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }

        throw new IOException("Could not create a unique backup file name.");
    }

    private async Task PruneAsync(string userId, string relativePath, CancellationToken ct)
    {
        var backups = await ListBackupsAsync(userId, relativePath, ct).ConfigureAwait(false);
        foreach (var backup in backups.Skip(UserFileBackupLimits.MaxVersionsPerFile))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var target = ResolveUserFilePath(userId, backup.RelativePath);
                var backupPath = GetBackupPath(target.UserRoot, target.RelativePath, backup.Id);
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Old file backup could not be pruned for {RelativePath} and {BackupId}",
                    backup.RelativePath,
                    backup.Id);
            }
        }
    }

    private ResolvedUserFilePath ResolveUserFilePath(string userId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("A file path is required.");
        }

        var userRoot = Path.GetFullPath(_userRootResolver(userId));
        var platformRelativePath = PathHelper.ToPlatformRelativePath(relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(userRoot, platformRelativePath));

        if (!PathHelper.IsPathWithinDirectory(fullPath, userRoot))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        if (PathHelper.HasSymlinkInPath(fullPath, userRoot))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        var canonicalRelativePath = PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(userRoot, fullPath));
        return new ResolvedUserFilePath(userRoot, canonicalRelativePath, fullPath);
    }

    private static string GetHistoryDirectory(string userRoot, string relativePath)
    {
        var platformRelativePath = PathHelper.ToPlatformRelativePath(relativePath);
        var parentPath = Path.GetDirectoryName(platformRelativePath);
        return string.IsNullOrWhiteSpace(parentPath)
            ? Path.Combine(userRoot, HistoryDirectory)
            : Path.Combine(userRoot, HistoryDirectory, parentPath);
    }

    private static string GetBackupPath(string userRoot, string relativePath, string backupId)
    {
        var historyDirectory = GetHistoryDirectory(userRoot, relativePath);
        var backupFileName = $"{Path.GetFileName(relativePath)}.{backupId}{BackupExtension}";
        var backupPath = Path.Combine(historyDirectory, backupFileName);
        // Backup files obey the same no-symlink boundary as their source files.
        if (PathHelper.HasSymlinkInPath(backupPath, userRoot))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        return backupPath;
    }

    private static void EnsureHistoryDirectorySafe(string userRoot, string historyDirectory)
    {
        var fullHistoryDirectory = Path.GetFullPath(historyDirectory);
        if (!PathHelper.IsPathWithinDirectory(fullHistoryDirectory, userRoot) ||
            PathHelper.HasSymlinkInPath(fullHistoryDirectory, userRoot))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }
    }

    private static bool TryParseBackupFileName(
        string fileName,
        string prefix,
        out string backupId,
        out DateTimeOffset createdUtc)
    {
        backupId = string.Empty;
        createdUtc = default;

        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(BackupExtension, StringComparison.Ordinal))
        {
            return false;
        }

        backupId = fileName[prefix.Length..^BackupExtension.Length];
        return TryParseBackupId(backupId, out createdUtc);
    }

    private static bool TryParseBackupId(string backupId, out DateTimeOffset createdUtc)
    {
        createdUtc = default;
        if (!BackupIdRegex.IsMatch(backupId))
        {
            return false;
        }

        var timestamp = GetTimestampPrefix(backupId);
        return DateTimeOffset.TryParseExact(
            timestamp,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out createdUtc);
    }

    private static DateTimeOffset ParseBackupIdUtc(string backupId)
    {
        return TryParseBackupId(backupId, out var createdUtc)
            ? createdUtc
            : throw new InvalidOperationException("Backup id is invalid.");
    }

    private static string GetTimestampPrefix(string backupId)
    {
        var suffixIndex = backupId.IndexOf('-', StringComparison.Ordinal);
        return suffixIndex < 0 ? backupId : backupId[..suffixIndex];
    }

    private static bool IsHistoryPath(string relativePath)
    {
        var firstSegment = relativePath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(firstSegment, HistoryDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of backup staging files; the original write failure is what matters.
        }
    }

    private sealed record ResolvedUserFilePath(
        string UserRoot,
        string RelativePath,
        string FullPath);
}
