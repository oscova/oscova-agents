using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Memory;

/// <summary>Coordinates long-term knowledge and user-local daily logs through the shared encrypted store.</summary>
public sealed class AgentMemoryService(
    Func<string, string> userRootResolver,
    string memoryRelativeRoot,
    long maxTextSizeBytes,
    Func<string, Task<DateOnly>> userDateResolver,
    IUserFileBackupService fileBackups,
    IContentProtector protector) : IAgentMemoryService
{
    /// <summary>Identifies the existing subtree reserved for short-term daily records.</summary>
    public const string DailyDirectoryName = "daily";
    private const string DateFormat = "yyyy-MM-dd";

    /// <inheritdoc />
    public EncryptedKnowledgeStore GetStore(string userId) =>
        new(userId, userRootResolver(userId), memoryRelativeRoot, maxTextSizeBytes, fileBackups, protector);

    /// <inheritdoc />
    public Task<string?> ReadLongTermMemoryAsync(string userId, CancellationToken ct = default) =>
        GetStore(userId).ReadAsync(ct: ct);

    /// <inheritdoc />
    public async Task<(DateOnly Date, string? Content)> ReadDailyLogAsync(string userId, int daysAgo = 0, CancellationToken ct = default)
    {
        if (daysAgo < 0) { throw new ArgumentOutOfRangeException(nameof(daysAgo)); }
        var date = (await userDateResolver(userId).ConfigureAwait(false)).AddDays(-daysAgo);
        return (date, await ReadDailyLogForDateAsync(userId, date, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<string?> ReadDailyLogForDateAsync(string userId, DateOnly date, CancellationToken ct = default) =>
        GetStore(userId).ReadAsync($"{DailyDirectoryName}/{date.ToString(DateFormat, CultureInfo.InvariantCulture)}", ct);

    /// <inheritdoc />
    public Task AppendDailyLogAsync(string userId, string logEntry, TimeZoneInfo? timeZone = null, CancellationToken ct = default)
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone ?? TimeZoneInfo.Utc);
        var date = DateOnly.FromDateTime(now).ToString(DateFormat, CultureInfo.InvariantCulture);
        // The shared writer keeps the date's metadata while appending to its complete encrypted body.
        return GetStore(userId).AppendAsync($"{DailyDirectoryName}/{date}",
            new Dictionary<string, object?> { ["type"] = "Daily Memory", ["title"] = date },
            $"\n## {now:HH:mm}\n{logEntry}\n", ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListDailyLogsDatesAsync(string userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<string> dates = GetStore(userId).ListConceptIds(DailyDirectoryName)
            .Select(id => id[(DailyDirectoryName.Length + 1)..])
            .Where(date => DateOnly.TryParseExact(date, DateFormat, out _)).ToArray();
        return Task.FromResult(dates);
    }
}