using Oscova.Agents.Core.Users;
using Oscova.Agents.Core.Utils;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>Applies shared size and file-signature checks to user-uploaded images.</summary>
internal static class ImageUploadValidation
{
    /// <summary>Validates the upload length and verifies its content signature.</summary>
    public static bool TryValidate(IFormFile? file, long maxSizeBytes, string assetLabel, out string error)
    {
        if (file is null || file.Length == 0)
        {
            error = "A file is required.";
            return false;
        }

        if (file.Length > maxSizeBytes)
        {
            error = $"{assetLabel} must be {ByteSize.Format(maxSizeBytes)} or smaller.";
            return false;
        }

        Span<byte> header = stackalloc byte[12];
        using var peek = file.OpenReadStream();
        int read = peek.Read(header);
        if (!ImageContent.IsSupportedImageHeader(header[..read]))
        {
            error = "File must be a PNG, JPEG, or WebP image.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
