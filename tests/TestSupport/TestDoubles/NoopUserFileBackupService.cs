using Oscova.Agents.FileBackups;

namespace Oscova.Agents.Tests.Infrastructure.TestDoubles;

/// <summary>Supplies inert backup behavior to tests that do not exercise file recovery.</summary>
public sealed class NoopUserFileBackupService : IUserFileBackupService
{
    public Task<UserFileBackupCaptureResult> CaptureBeforeMutationAsync(
        string userId,
        string relativePath,
        CancellationToken ct = default) =>
        Task.FromResult(new UserFileBackupCaptureResult(UserFileBackupCaptureStatus.SkippedMissing, null));

    public Task<IReadOnlyList<UserFileBackupEntry>> ListBackupsAsync(
        string userId,
        string relativePath,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserFileBackupEntry>>([]);

    public Task<UserFileBackupContent> ReadBackupAsync(
        string userId,
        string relativePath,
        string backupId,
        CancellationToken ct = default) =>
        throw new FileNotFoundException("Backup not found.");

    public Task<UserFileBackupEntry> RestoreBackupAsync(
        string userId,
        string relativePath,
        string backupId,
        CancellationToken ct = default) =>
        throw new FileNotFoundException("Backup not found.");
}
