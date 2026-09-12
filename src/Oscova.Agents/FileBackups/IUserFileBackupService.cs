using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.FileBackups;

/// <summary>
/// Captures and restores bounded text snapshots for caller-selected user files.
/// </summary>
public interface IUserFileBackupService
{
    /// <summary>
    /// Captures the current file contents before a caller mutates the file.
    /// </summary>
    Task<UserFileBackupCaptureResult> CaptureBeforeMutationAsync(
        string userId,
        string relativePath,
        CancellationToken ct = default);

    /// <summary>
    /// Lists captured versions for the original relative file path.
    /// </summary>
    Task<IReadOnlyList<UserFileBackupEntry>> ListBackupsAsync(
        string userId,
        string relativePath,
        CancellationToken ct = default);

    /// <summary>
    /// Reads one stored backup and its text content.
    /// </summary>
    Task<UserFileBackupContent> ReadBackupAsync(
        string userId,
        string relativePath,
        string backupId,
        CancellationToken ct = default);

    /// <summary>Restores a backup; cancellation is honored before the final mutation, then the write completes.</summary>
    Task<UserFileBackupEntry> RestoreBackupAsync(
        string userId,
        string relativePath,
        string backupId,
        CancellationToken ct = default);
}
