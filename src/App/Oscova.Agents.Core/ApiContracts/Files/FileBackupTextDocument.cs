namespace Oscova.Agents.Core.ApiContracts.Files;

/// <summary>
/// Returns a restore point plus the text captured in that backup.
/// </summary>
public sealed class FileBackupTextDocument
{
    public FileBackupSummary Backup { get; set; } = new();
    public string Content { get; set; } = string.Empty;
}
