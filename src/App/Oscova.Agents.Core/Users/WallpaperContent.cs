using Oscova.Agents.Core.Utils;

namespace Oscova.Agents.Core.Users;

/// <summary>Defines the upload limits for user wallpapers.</summary>
public static class WallpaperContent
{
    public const long MaxUploadSizeBytes = 10 * ByteSize.OneMegabyte;
    public const long MaxRequestSizeBytes = MaxUploadSizeBytes + (8 * ByteSize.OneKilobyte);
}
