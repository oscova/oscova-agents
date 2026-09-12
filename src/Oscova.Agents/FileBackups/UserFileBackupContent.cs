namespace Oscova.Agents.FileBackups;

/// <summary>
/// Carries a stored backup and its text content for comparison or restore.
/// </summary>
public sealed record UserFileBackupContent(
    UserFileBackupEntry Entry,
    string Content);
