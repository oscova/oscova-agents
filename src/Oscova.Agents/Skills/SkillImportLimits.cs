namespace Oscova.Agents.Skills;

/// <summary>Defines shared size limits for skill package uploads and archive expansion.</summary>
public static class SkillImportLimits
{
    private const long OneMegabyte = 1024L * 1024L;

    /// <summary>Gets the maximum accepted upload size in bytes.</summary>
    public const long MaxUploadSizeBytes = 10 * OneMegabyte;

    /// <summary>Gets the maximum declared expanded archive size in bytes.</summary>
    public const long MaxUncompressedZipSizeBytes = 30 * OneMegabyte;
}
