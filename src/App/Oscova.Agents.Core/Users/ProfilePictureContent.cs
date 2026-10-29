using Oscova.Agents.Core.Utils;

namespace Oscova.Agents.Core.Users;

/// <summary>Defines the upload limits for profile pictures.</summary>
public static class ProfilePictureContent
{
    public const long MaxUploadSizeBytes = 2 * ByteSize.OneMegabyte;
    public const long MaxRequestSizeBytes = MaxUploadSizeBytes + (8 * ByteSize.OneKilobyte);

}
