using System.Text.Json;
using Oscova.Agents.Core.Logging;

namespace Oscova.Agents.Infrastructure.Logging;

public static class LogReaderService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // NLog level hierarchy (index = severity, higher = more severe)
    private static readonly string[] LevelHierarchy =
        ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"];

    // Safety limit to prevent loading excessively large log files into memory.
    private const int MaxLinesToRead = 50000;

    /// <summary>
    /// Returns available .jsonl log file names sorted by date descending (newest first).
    /// </summary>
    public static IReadOnlyList<string> GetLogFiles(string rootDirectory, string filePrefix)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return [];
        }

        return [.. new DirectoryInfo(rootDirectory)
            .GetFiles($"{filePrefix}-*.jsonl")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.Name)];
    }

    /// <summary>
    /// Reads, deserializes, filters, and paginates log entries from a .jsonl file under the given root.
    /// </summary>
    public static async Task<LogPage> ReadLogsAsync(
        string rootDirectory,
        string filePrefix,
        string? fileName,
        string? levelFilter,
        string? searchTerm,
        int page = 1,
        int pageSize = 100)
    {
        var files = GetLogFiles(rootDirectory, filePrefix);

        if (files.Count == 0)
        {
            return EmptyPage(fileName ?? string.Empty, page);
        }

        // Use specified file or fall back to the latest
        var targetFile = fileName ?? files[0];
        var filePath = Path.Combine(rootDirectory, targetFile);

        if (!File.Exists(filePath))
        {
            return EmptyPage(targetFile, page);
        }

        var allEntries = await ReadEntriesAsync(filePath).ConfigureAwait(false);

        // Apply level filter (hierarchical: selecting INFO means INFO + WARN + ERROR + FATAL)
        if (!string.IsNullOrWhiteSpace(levelFilter) &&
            !string.Equals(levelFilter, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            var minIndex = Array.FindIndex(LevelHierarchy,
                l => string.Equals(l, levelFilter, StringComparison.OrdinalIgnoreCase));

            if (minIndex >= 0)
            {
                allEntries = allEntries
                    .Where(e =>
                    {
                        var entryIndex = Array.FindIndex(LevelHierarchy,
                            l => string.Equals(l, e.Level, StringComparison.OrdinalIgnoreCase));
                        return entryIndex >= minIndex;
                    })
                    .ToList();
            }
        }

        // Apply text search across the stable envelope, metadata, and exception payload.
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            allEntries = allEntries
                .Where(e => e.MatchesSearch(term))
                .ToList();
        }

        var totalCount = allEntries.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);

        // Newest entries first, then paginate
        var entries = allEntries
            .OrderByDescending(e => e.Time)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new LogPage
        {
            Entries = entries,
            TotalCount = totalCount,
            CurrentPage = page,
            TotalPages = totalPages,
            FileName = targetFile
        };
    }

    /// <summary>
    /// Clears the specified log file (truncates or deletes) under the given root directory.
    /// </summary>
    public static async Task ClearLogFileAsync(string rootDirectory, string fileName)
    {
        var jsonlPath = Path.Combine(rootDirectory, fileName);
        await TruncateOrDeleteAsync(jsonlPath).ConfigureAwait(false);
    }

    private static async Task TruncateOrDeleteAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var fs = new FileStream(filePath, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
            await using (fs.ConfigureAwait(false))
            {
            }
        }
        catch
        {
            // Fall back to delete if truncate fails; logger will recreate on next write
            File.Delete(filePath);
        }
    }

    private static async Task<List<LogEntry>> ReadEntriesAsync(string filePath)
    {
        var entries = new Queue<LogEntry>();

        // Open with ReadWrite share so NLog can continue writing
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Open with ReadWrite share so NLog can continue writing
        await using (fs.ConfigureAwait(false))
        {
            using var reader = new StreamReader(fs);

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<LogEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    if (entries.Count >= MaxLinesToRead)
                    {
                        entries.Dequeue();
                    }

                    entries.Enqueue(entry);
                }
            }
            catch
            {
                // Skip malformed lines silently
            }
        }

        return [.. entries];
        }
    }

    private static LogPage EmptyPage(string fileName, int page) => new()
    {
        Entries = [],
        TotalCount = 0,
        CurrentPage = page,
        TotalPages = 1,
        FileName = fileName
    };
}
