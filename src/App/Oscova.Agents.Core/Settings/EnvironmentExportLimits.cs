using Oscova.Agents.Core.Utils;

namespace Oscova.Agents.Core.Settings;

public static class EnvironmentExportLimits
{
    public const long MaxUploadSizeBytes = 500 * ByteSize.OneMegabyte;
    public const long MaxUncompressedZipSizeBytes = 2 * ByteSize.OneGigabyte;
}
