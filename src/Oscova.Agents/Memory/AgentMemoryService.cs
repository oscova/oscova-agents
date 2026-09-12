using Oscova.Agents.FileBackups;
using Oscova.Agents.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Memory
{
    /// <summary>Stores bounded long-term, topic, and daily memory beneath caller-selected user roots.</summary>
    public class AgentMemoryService : IAgentMemoryService
    {
        private const string MemoryRoot = "memory";
        private const string LongTermIndexFileName = "index.md";
        private const string DailyDirectoryName = "daily";
        private const string DailyLogDateFormat = "yyyy-MM-dd";

        private static readonly Regex TopicSlugPattern = new(
            "^[a-z0-9]+(?:-[a-z0-9]+)*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        private static readonly HashSet<string> ReservedTopicNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "index",
            "daily",
            "longterm",
            "today",
            "yesterday"
        };

        private readonly Func<string, string> _userRootResolver;
        private readonly long _maxTextSizeBytes;
        private readonly Func<string, Task<DateOnly>> _userDateResolver;
        private readonly IUserFileBackupService _fileBackups;
        // Serialize a user's backup-plus-write transactions across retained and transient service scopes.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationGates = new(StringComparer.Ordinal);

        /// <summary>Creates file-backed agent memory beneath caller-selected user roots.</summary>
        public AgentMemoryService(
            Func<string, string> userRootResolver,
            long maxTextSizeBytes,
            Func<string, Task<DateOnly>> userDateResolver,
            IUserFileBackupService fileBackups)
        {
            _userRootResolver = userRootResolver ?? throw new ArgumentNullException(nameof(userRootResolver));
            _maxTextSizeBytes = maxTextSizeBytes;
            _userDateResolver = userDateResolver ?? throw new ArgumentNullException(nameof(userDateResolver));
            _fileBackups = fileBackups;
        }

        /// <inheritdoc />
        public async Task<string?> ReadLongTermMemoryAsync(string userId, CancellationToken ct = default)
        {
            var path = GetLongTermIndexPath();
            if (!FileExists(userId, path))
            {
                return null;
            }

            return await ReadFileTextForRuntimeAsync(userId, path, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task AppendLongTermMemoryAsync(string userId, string memoryEntry, string? topic = null, CancellationToken ct = default)
        {
            await RunMutationAsync(userId, async () =>
            {
                if (string.IsNullOrWhiteSpace(topic))
                {
                    var path = GetLongTermIndexPath();
                    await CaptureMemoryFileAsync(userId, path, ct).ConfigureAwait(false);
                    await AppendToFileAsync(userId, path, FormatLongTermEntry(memoryEntry)).ConfigureAwait(false);
                    return;
                }

                var slug = NormalizeTopicSlugOrThrow(topic);
                await EnsureTopicLinkAsync(userId, slug, ct).ConfigureAwait(false);
                var topicPath = GetTopicPath(slug);
                await CaptureMemoryFileAsync(userId, topicPath, ct).ConfigureAwait(false);
                await AppendToFileAsync(userId, topicPath, FormatLongTermEntry(memoryEntry)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task WriteLongTermMemoryAsync(string userId, string memoryContent, CancellationToken ct = default)
        {
            await RunMutationAsync(userId, async () =>
            {
                var path = GetLongTermIndexPath();
                await CaptureMemoryFileAsync(userId, path, ct).ConfigureAwait(false);
                await WriteFileTextAsync(userId, path, memoryContent).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<string?> ReadTopicMemoryAsync(string userId, string topic, CancellationToken ct = default)
        {
            var slug = NormalizeTopicSlugOrThrow(topic);
            var path = GetTopicPath(slug);
            if (!FileExists(userId, path))
            {
                return null;
            }

            return await ReadFileTextForRuntimeAsync(userId, path, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<MemoryForgetResult> ForgetLongTermMemoryAsync(string userId, string entryText, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(entryText))
            {
                throw new AgentMemoryValidationException("Memory text is required.");
            }

            return await RunMutationAsync(userId, async () =>
            {
                var filesUpdated = 0;
                filesUpdated += await RemoveMatchingLinesAsync(userId, GetLongTermIndexPath(), entryText, ct).ConfigureAwait(false) ? 1 : 0;

                foreach (var topicFile in await ListTopicFilesAsync(userId, ct).ConfigureAwait(false))
                {
                    filesUpdated += await RemoveMatchingLinesAsync(userId, $"{MemoryRoot}/{topicFile}", entryText, ct).ConfigureAwait(false) ? 1 : 0;
                }

                return new MemoryForgetResult
                {
                    FilesUpdated = filesUpdated
                };
            }, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task AppendDailyLogAsync(string userId, string logEntry, TimeZoneInfo? timeZone = null, CancellationToken ct = default)
        {
            await RunMutationAsync(userId, async () =>
            {
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone ?? TimeZoneInfo.Utc);
                var formattedContent = $"\n## {now:HH:mm}\n{logEntry}\n";
                var fileName = $"{DateOnly.FromDateTime(now).ToString(DailyLogDateFormat)}.md";
                var path = $"{MemoryRoot}/{DailyDirectoryName}/{fileName}";
                await CaptureMemoryFileAsync(userId, path, ct).ConfigureAwait(false);
                await AppendToFileAsync(userId, path, formattedContent).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        private static async Task RunMutationAsync(string userId, Func<Task> mutation, CancellationToken ct)
        {
            await RunMutationAsync(userId, async () =>
            {
                await mutation().ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }

        private static async Task<T> RunMutationAsync<T>(string userId, Func<Task<T>> mutation, CancellationToken ct)
        {
            var gate = MutationGates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await mutation().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<(DateOnly Date, string? Content)> ReadDailyLogAsync(string userId, int daysAgo = 0, CancellationToken ct = default)
        {
            if (daysAgo < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(daysAgo), "Days ago cannot be negative.");
            }

            var date = (await GetUserTodayAsync(userId).ConfigureAwait(false)).AddDays(-daysAgo);
            var content = await ReadDailyLogForDateAsync(userId, date, ct).ConfigureAwait(false);
            return (date, content);
        }

        /// <inheritdoc />
        public async Task<string?> ReadDailyLogForDateAsync(string userId, DateOnly date, CancellationToken ct = default)
        {
            var path = $"{MemoryRoot}/{DailyDirectoryName}/{date.ToString(DailyLogDateFormat)}.md";
            if (!FileExists(userId, path))
            {
                return null;
            }

            return await ReadFileTextForRuntimeAsync(userId, path, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> ListDailyLogsDatesAsync(string userId, CancellationToken ct = default)
        {
            var entries = ListFileNames(userId, $"{MemoryRoot}/{DailyDirectoryName}");
            var dates = new List<string>();
            foreach (var entry in entries)
            {
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(entry), DailyLogDateFormat, out _))
                {
                    dates.Add(Path.GetFileNameWithoutExtension(entry));
                }
            }
            return await Task.FromResult<IReadOnlyList<string>>(dates).ConfigureAwait(false);
        }

        private async Task EnsureTopicLinkAsync(string userId, string slug, CancellationToken ct)
        {
            var indexPath = GetLongTermIndexPath();
            var existing = FileExists(userId, indexPath)
                ? await ReadFileTextAsync(userId, indexPath)
.ConfigureAwait(false) : string.Empty;
            var link = $"- [{slug}]({slug}.md)";
            if (existing.Contains(link, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var builder = new StringBuilder(existing.TrimEnd());
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            if (!existing.Contains("## Topics", StringComparison.OrdinalIgnoreCase))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.AppendLine("## Topics");
            }

            builder.AppendLine(link);
            await CaptureMemoryFileAsync(userId, indexPath, ct).ConfigureAwait(false);
            await WriteFileTextAsync(userId, indexPath, builder.ToString()).ConfigureAwait(false);
        }

        private async Task<bool> RemoveMatchingLinesAsync(string userId, string path, string entryText, CancellationToken ct)
        {
            if (!FileExists(userId, path))
            {
                return false;
            }

            var content = await ReadFileTextAsync(userId, path).ConfigureAwait(false);
            var lines = content.Split('\n').ToList();
            var originalCount = lines.Count;
            lines.RemoveAll(line => line.Contains(entryText, StringComparison.OrdinalIgnoreCase));
            if (lines.Count == originalCount)
            {
                return false;
            }

            await CaptureMemoryFileAsync(userId, path, ct).ConfigureAwait(false);
            await WriteFileTextAsync(userId, path, string.Join('\n', lines).TrimEnd() + Environment.NewLine).ConfigureAwait(false);
            return true;
        }

        private Task CaptureMemoryFileAsync(string userId, string relativePath, CancellationToken ct)
        {
            return _fileBackups.CaptureBeforeMutationAsync(userId, relativePath, ct);
        }

        private async Task<IReadOnlyList<string>> ListTopicFilesAsync(string userId, CancellationToken ct)
        {
            var entries = ListFileNames(userId, MemoryRoot);
            return await Task.FromResult<IReadOnlyList<string>>([.. entries
                .Where(name => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, LongTermIndexFileName, StringComparison.OrdinalIgnoreCase) && IsValidTopicSlug(Path.GetFileNameWithoutExtension(name)))])
                .ConfigureAwait(false);
        }

        private static string NormalizeTopicSlugOrThrow(string topic)
        {
            var slug = topic.Trim().ToLowerInvariant();
            if (!IsValidTopicSlug(slug))
            {
                throw new AgentMemoryValidationException("Topic must be a lowercase kebab-case slug and cannot be a reserved memory target.");
            }

            return slug;
        }

        private static bool IsValidTopicSlug(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug) ||
                ReservedTopicNames.Contains(slug) ||
                DateOnly.TryParseExact(slug, DailyLogDateFormat, out _))
            {
                return false;
            }

            return TopicSlugPattern.IsMatch(slug);
        }

        private static string FormatLongTermEntry(string memoryEntry)
        {
            return "\n" + memoryEntry.Trim() + "\n";
        }

        private static string GetLongTermIndexPath()
        {
            return $"{MemoryRoot}/{LongTermIndexFileName}";
        }

        private static string GetTopicPath(string slug)
        {
            return $"{MemoryRoot}/{slug}.md";
        }

        private bool FileExists(string userId, string relativePath)
        {
            return File.Exists(ResolveMemoryPath(userId, relativePath));
        }

        private async Task<string> ReadFileTextForRuntimeAsync(string userId, string relativePath, CancellationToken ct)
        {
            var fullPath = ResolveMemoryPath(userId, relativePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("File not found.");
            }

            using var reader = new StreamReader(fullPath);
            var buffer = new char[checked((int)_maxTextSizeBytes)];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await reader
                    .ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return new string(buffer, 0, totalRead);
        }

        private async Task<string> ReadFileTextAsync(string userId, string relativePath)
        {
            var fullPath = ResolveMemoryPath(userId, relativePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("File not found.");
            }

            if (new FileInfo(fullPath).Length > _maxTextSizeBytes)
            {
                throw new AgentMemoryValidationException($"File is too large to edit (max {FormatByteSize(_maxTextSizeBytes)}).");
            }

            return await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
        }

        private async Task WriteFileTextAsync(string userId, string relativePath, string content)
        {
            var fullPath = ResolveMemoryPath(userId, relativePath);
            if (Encoding.UTF8.GetByteCount(content) > _maxTextSizeBytes)
            {
                throw new AgentMemoryValidationException($"Content is too large to save (max {FormatByteSize(_maxTextSizeBytes)}).");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content).ConfigureAwait(false);
        }

        private async Task AppendToFileAsync(string userId, string relativePath, string content)
        {
            var fullPath = ResolveMemoryPath(userId, relativePath);
            var newContentBytes = Encoding.UTF8.GetByteCount(content);
            if (newContentBytes > _maxTextSizeBytes)
            {
                throw new AgentMemoryValidationException($"Content is too large to append (max {FormatByteSize(_maxTextSizeBytes)}).");
            }

            var existingLength = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
            if (existingLength + newContentBytes > _maxTextSizeBytes)
            {
                throw new AgentMemoryValidationException($"Resulting file is too large to append (max {FormatByteSize(_maxTextSizeBytes)}).");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.AppendAllTextAsync(fullPath, content).ConfigureAwait(false);
        }

        private IReadOnlyList<string> ListFileNames(string userId, string relativePath)
        {
            var fullPath = ResolveMemoryPath(userId, relativePath);
            if (!Directory.Exists(fullPath))
            {
                return [];
            }

            return [.. Directory
                .EnumerateFiles(fullPath)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)!];
        }

        private string ResolveMemoryPath(string userId, string relativePath)
        {
            var userRoot = Path.GetFullPath(_userRootResolver(userId));
            var fullPath = Path.GetFullPath(Path.Combine(
                userRoot,
                PathHelper.ToPlatformRelativePath(relativePath)));

            if (!PathHelper.IsPathWithinDirectory(fullPath, userRoot) ||
                PathHelper.HasSymlinkInPath(fullPath, userRoot))
            {
                throw new UnauthorizedAccessException("Access denied.");
            }

            return fullPath;
        }

        private static string FormatByteSize(long bytes)
        {
            var value = (double)bytes;
            string[] units = ["B", "KB", "MB", "GB"];
            var unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return unitIndex == 0
                ? $"{value:0} {units[unitIndex]}"
                : $"{value:0.#} {units[unitIndex]}";
        }

        private Task<DateOnly> GetUserTodayAsync(string userId)
        {
            return _userDateResolver(userId);
        }
    }
}
