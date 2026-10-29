namespace Oscova.Agents.Core.Backup;

/// <summary>
/// Manifest stored at the root of a decrypted Oscova environment backup.
/// </summary>
public sealed class EnvironmentBundleManifest
{
    public const int CurrentBundleFormatVersion = 1;
    public const string EntryName = "manifest.json";

    public int BundleFormatVersion { get; set; } = CurrentBundleFormatVersion;

    public int? DataFormatVersion { get; set; }

    public string AppVersion { get; set; } = string.Empty;

    public DateTimeOffset ExportedAtUtc { get; set; }

    public string ExportedByUserId { get; set; } = string.Empty;
}
