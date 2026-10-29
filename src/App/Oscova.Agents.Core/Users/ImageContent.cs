namespace Oscova.Agents.Core.Users;

/// <summary>Identifies supported browser image formats from trusted file signatures.</summary>
public static class ImageContent
{
    public const string PngContentType = "image/png";
    public const string JpegContentType = "image/jpeg";
    public const string WebPContentType = "image/webp";
    public const string DefaultContentType = "application/octet-stream";

    /// <summary>Returns true when the header matches a supported browser image format.</summary>
    public static bool IsSupportedImageHeader(ReadOnlySpan<byte> header)
    {
        return SniffContentType(header) != DefaultContentType;
    }

    /// <summary>Sniffs PNG, JPEG, and WebP content types from an image header.</summary>
    public static string SniffContentType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return PngContentType;
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return JpegContentType;
        }

        if (header.Length >= 12
            && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
        {
            return WebPContentType;
        }

        return DefaultContentType;
    }
}

/// <summary>Result of an image read whose stream the caller owns and disposes.</summary>
public sealed class ImageReadResult(Stream stream, string contentType)
{
    public Stream Stream { get; } = stream;
    public string ContentType { get; } = contentType;
}
