using Oscova.Agents.Core.Users;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Users;

/// <summary>Owns shared file operations for seeded profile pictures and wallpapers.</summary>
internal static class SeededImageFileStore
{
    public static IReadOnlyList<string> GetAvailableSeedNames(string seedDirectory, string fileExtension)
    {
        if (!Directory.Exists(seedDirectory))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(seedDirectory, $"*{fileExtension}", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Order(StringComparer.Ordinal)];
    }

    public static string GetExistingSeedFilePath(
        string seedDirectory,
        string seedName,
        string fileExtension,
        string assetName)
    {
        var seedFilePath = Directory.Exists(seedDirectory)
            ? Directory
                .EnumerateFiles(seedDirectory, $"*{fileExtension}", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path =>
                    string.Equals(Path.GetFileNameWithoutExtension(path), seedName, StringComparison.Ordinal))
            : null;
        if (seedFilePath is null)
        {
            throw new FileNotFoundException($"Seed {assetName} '{seedName}{fileExtension}' was not found.");
        }

        return seedFilePath;
    }

    public static void EnsureSeeded(string destinationFilePath, Func<string> resolveSeedFilePath)
    {
        if (File.Exists(destinationFilePath))
        {
            return;
        }

        // Existing images must remain usable even when the seed package is unavailable.
        var seedFilePath = resolveSeedFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        try
        {
            File.Copy(seedFilePath, destinationFilePath);
        }
        catch (IOException) when (File.Exists(destinationFilePath))
        {
            // Another request seeded the same image first.
        }
    }

    public static void CopySeed(string seedFilePath, string destinationFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        File.Copy(seedFilePath, destinationFilePath, overwrite: true);
    }

    public static async Task WriteAsync(string filePath, Stream content, CancellationToken ct)
    {
        await FileUtil.WriteStreamAtomicAsync(filePath, content, ct).ConfigureAwait(false);
    }

    public static ImageReadResult Open(string filePath)
    {
        var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[12];
        int read = fileStream.Read(header);
        var contentType = ImageContent.SniffContentType(header[..read]);
        fileStream.Position = 0;
        return new ImageReadResult(fileStream, contentType);
    }
}
