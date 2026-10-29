namespace Oscova.Agents.Core.ApiContracts.Files;

/// <summary>
/// Describes one available restore point for a user-editable file.
/// </summary>
public sealed class FileBackupSummary
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public long SizeBytes { get; set; }
}
