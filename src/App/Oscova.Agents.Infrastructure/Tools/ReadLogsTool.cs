using System.ComponentModel;
using System.Globalization;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Logging;
using Oscova.Agents.Infrastructure.Logging;

namespace Oscova.Agents.Infrastructure.Tools;

/// <summary>
/// Exposes recent log entries for the calling user as an agent tool. Bound per user-scope so
/// the tool can only ever read the calling user's partitioned log folder.
/// </summary>
[DisplayName("Logs")]
public sealed class ReadLogsTool(string userId, IUserDirectoryService userDirectories) : IAgentToolset
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int DefaultSinceMinutes = 60;
    private const int MaxSinceMinutes = 7 * 24 * 60;

    [DisplayName("read")]
    [AgentTool]
    [Description("Reads the calling user's recent application log entries. " +
                 "Use when the user asks 'what failed?', 'why did X not work?', or wants to see recent errors.")]
    public async Task<object> ReadAsync(
        [Description("Minimum level: trace|debug|info|warn|error|fatal. Defaults to all.")]
            string? level = null,
        [Description("""
            Only return entries from the last N minutes.
            Default 60.
            Values are clamped to 1-10080 minutes.
            """)]
            int sinceMinutes = DefaultSinceMinutes,
        [Description("Optional substring filter applied to message/metadata/exception.")]
            string? search = null,
        [Description("Max entries to return. Default 50, max 200.")]
            int limit = DefaultLimit)
    {
        var root = userDirectories.GetLogsDirectoryPath(userId);
        var effectiveLimit = Math.Clamp(limit, 1, MaxLimit);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(sinceMinutes, 1, MaxSinceMinutes));
        var entries = new List<LogEntry>();

        foreach (var fileName in LogReaderService.GetLogFiles(root, "user"))
        {
            var page = await LogReaderService.ReadLogsAsync(
                root,
                filePrefix: "user",
                fileName: fileName,
                levelFilter: level,
                searchTerm: search,
                page: 1,
                pageSize: effectiveLimit).ConfigureAwait(false);

            entries.AddRange(page.Entries
                .Where(entry => !TryParseTimestamp(entry.Time, out var timestamp) || timestamp >= cutoff));
        }

        var filtered = entries
            .OrderByDescending(entry => TryParseTimestamp(entry.Time, out var timestamp) ? timestamp : DateTimeOffset.MaxValue)
            .Take(effectiveLimit)
            .ToList();

        return new
        {
            count = filtered.Count,
            entries = filtered.Select(entry => new
            {
                time = entry.Time,
                level = entry.Level,
                logger = entry.ShortLogger,
                message = entry.Message,
                exception = entry.Exception is null ? null : new
                {
                    type = entry.Exception.Type,
                    message = entry.Exception.Message
                }
            })
        };
    }

    // NLog ${longdate} writes local time with no offset; parse as local so the cutoff comparison matches UtcNow.
    private static bool TryParseTimestamp(string raw, out DateTimeOffset value)
    {
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value);
    }
}
