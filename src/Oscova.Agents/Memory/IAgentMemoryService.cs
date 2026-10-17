using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Memory;

/// <summary>Supplies a user's encrypted long-term bundle and existing daily-memory lifecycle.</summary>
public interface IAgentMemoryService
{
    /// <summary>Returns the shared knowledge implementation bound to this user's memory root.</summary>
    EncryptedKnowledgeStore GetStore(string userId);

    /// <summary>Reads the complete long-term entry index.</summary>
    Task<string?> ReadLongTermMemoryAsync(string userId, CancellationToken ct = default);

    /// <summary>Reads a daily log relative to the user's current local date.</summary>
    Task<(DateOnly Date, string? Content)> ReadDailyLogAsync(string userId, int daysAgo = 0, CancellationToken ct = default);

    /// <summary>Reads the daily concept for an explicit local date.</summary>
    Task<string?> ReadDailyLogForDateAsync(string userId, DateOnly date, CancellationToken ct = default);

    /// <summary>Appends short-term information to today's encrypted daily concept.</summary>
    Task AppendDailyLogAsync(string userId, string logEntry, TimeZoneInfo? timeZone = null, CancellationToken ct = default);

    /// <summary>Lists actual saved daily dates.</summary>
    Task<IReadOnlyList<string>> ListDailyLogsDatesAsync(string userId, CancellationToken ct = default);
}