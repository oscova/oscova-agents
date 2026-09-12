using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Oscova.Agents.Utils;

/// <summary>
/// Validates archive entries before extraction and resolves safe destination paths under a single root.
/// </summary>
public static class ZipArchiveExtractionPlan
{
    /// <summary>
    /// Builds a validated extraction plan for the supplied archive entries.
    /// </summary>
    public static IReadOnlyList<ZipArchiveExtractionItem> Create(
        ZipArchive archive,
        string destinationRoot,
        long maxUncompressedSizeBytes,
        Func<ZipArchiveEntry, string?> relativePathSelector)
    {
        var extractionItems = new List<ZipArchiveExtractionItem>();
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressedSize = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string? relativePath = relativePathSelector(entry);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException($"Archive entry '{entry.FullName}' is rooted and cannot be imported.");
            }

            string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
            if (!PathHelper.IsPathWithinDirectory(destinationPath, destinationRoot))
            {
                throw new ArgumentException($"Archive entry '{entry.FullName}' escapes the extraction directory.");
            }

            string normalizedDestination = destinationPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!extractedPaths.Add(normalizedDestination))
            {
                throw new ArgumentException($"Archive entry '{entry.FullName}' resolves to a duplicate path.");
            }

            totalUncompressedSize += entry.Length;
            if (totalUncompressedSize > maxUncompressedSizeBytes)
            {
                throw new ArgumentException($"The archive exceeds the {FormatByteSize(maxUncompressedSizeBytes)} uncompressed size limit.");
            }

            extractionItems.Add(new ZipArchiveExtractionItem(
                entry,
                relativePath,
                destinationPath,
                relativePath.EndsWith('/')));
        }

        return extractionItems;
    }

    private static string FormatByteSize(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.#} {units[unitIndex]}";
    }
}

/// <summary>
/// Represents a validated archive entry and the destination path it resolves to.
/// </summary>
public sealed record ZipArchiveExtractionItem(
    ZipArchiveEntry Entry,
    string RelativePath,
    string DestinationPath,
    bool IsDirectory);
