using Oscova.Agents.Core.Utils;

namespace Oscova.Agents.Core.Users;

/// <summary>
/// Shared upload and edit limits for user-managed workspace files.
/// </summary>
public static class UserFileLimits
{
    public const long MaxUploadSizeBytes = 10 * ByteSize.OneMegabyte;
    public const long MaxRequestSizeBytes = MaxUploadSizeBytes + (8 * ByteSize.OneKilobyte);
    public const long MaxEditSizeBytes = 512 * ByteSize.OneKilobyte;

    /// <summary>Stored size of an encrypted file whose plaintext is <see cref="MaxEditSizeBytes"/>.</summary>
    public const long MaxEncryptedEditSizeBytes = 45 + (4 * ((MaxEditSizeBytes + 2) / 3));
}
