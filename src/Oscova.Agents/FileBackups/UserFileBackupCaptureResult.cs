namespace Oscova.Agents.FileBackups;

/// <summary>
/// Describes the outcome of a requested pre-mutation backup capture.
/// </summary>
public enum UserFileBackupCaptureStatus
{
    /// <summary>Indicates that a backup was captured.</summary>
    Captured,
    /// <summary>Indicates that the source file does not exist.</summary>
    SkippedMissing,
    /// <summary>Indicates that the source path is a directory.</summary>
    SkippedDirectory,
    /// <summary>Indicates that the source path is inside file history.</summary>
    SkippedHistoryPath,
    /// <summary>Indicates that the caller's file policy rejected the source path.</summary>
    SkippedUnsupportedExtension,
    /// <summary>Indicates that the source file exceeds the configured size limit.</summary>
    SkippedOversized,
    /// <summary>Indicates that the latest backup already contains the source text.</summary>
    SkippedDuplicate
}

/// <summary>
/// Reports whether a requested pre-mutation backup was written or intentionally skipped.
/// </summary>
public sealed record UserFileBackupCaptureResult(
    UserFileBackupCaptureStatus Status,
    UserFileBackupEntry? Entry);
