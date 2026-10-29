namespace Oscova.Agents.Core.Backup;

/// <summary>
/// Identifies the persisted environment contract independently of an app release.
/// </summary>
public sealed class EnvironmentDataFormat
{
    public const int CurrentVersion = 1;
    public const string FileName = "environment-format.json";

    public int DataFormatVersion { get; init; }

    public string AppVersion { get; init; } = string.Empty;
}
