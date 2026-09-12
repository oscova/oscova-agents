using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Memory;

/// <summary>
/// Provides access to the agent's persistent memory files for a given user.
/// </summary>
public interface IAgentMemoryService
{
    /// <summary>
    /// Reads the user's durable long-term memory text.
    /// </summary>
    /// <param name="userId">The user whose memory should be read.</param>
    /// <param name="ct">A token that can cancel the read.</param>
    /// <returns>The stored memory text, or <see langword="null"/> when no memory exists.</returns>
    Task<string?> ReadLongTermMemoryAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Appends a new entry to the user's long-term index or a topic file.
    /// </summary>
    /// <param name="userId">The user whose memory should be updated.</param>
    /// <param name="memoryEntry">The entry text to append.</param>
    /// <param name="topic">Optional lowercase kebab-case topic slug.</param>
    /// <param name="ct">A token that can cancel before the final mutation; an admitted write completes.</param>
    Task AppendLongTermMemoryAsync(string userId, string memoryEntry, string? topic = null, CancellationToken ct = default);

    /// <summary>
    /// Replaces the user's long-term memory with the supplied content.
    /// </summary>
    /// <param name="userId">The user whose memory should be replaced.</param>
    /// <param name="memoryContent">The complete long-term memory text.</param>
    /// <param name="ct">A token that can cancel before the final mutation; an admitted write completes.</param>
    Task WriteLongTermMemoryAsync(string userId, string memoryContent, CancellationToken ct = default);

    /// <summary>
    /// Reads a long-term memory topic file.
    /// </summary>
    /// <param name="userId">The user whose topic should be read.</param>
    /// <param name="topic">The lowercase kebab-case topic slug.</param>
    /// <param name="ct">A token that can cancel the read.</param>
    /// <returns>The stored topic text, or <see langword="null"/> when no topic exists.</returns>
    Task<string?> ReadTopicMemoryAsync(string userId, string topic, CancellationToken ct = default);

    /// <summary>
    /// Removes matching text from the long-term index and topic files.
    /// </summary>
    /// <param name="userId">The user whose memory should be updated.</param>
    /// <param name="entryText">The text to remove from matching lines.</param>
    /// <param name="ct">A token that can cancel before the final mutation; an admitted update completes.</param>
    Task<MemoryForgetResult> ForgetLongTermMemoryAsync(string userId, string entryText, CancellationToken ct = default);

    /// <summary>
    /// Reads the user's daily log relative to the user's current local date.
    /// </summary>
    /// <param name="userId">The user whose daily log should be read.</param>
    /// <param name="daysAgo">How many days before the user's current local date should be read.</param>
    /// <param name="ct">A token that can cancel the read.</param>
    /// <returns>The local date that was read and the stored daily log text, or <see langword="null"/> when no log exists.</returns>
    Task<(DateOnly Date, string? Content)> ReadDailyLogAsync(string userId, int daysAgo = 0, CancellationToken ct = default);

    /// <summary>
    /// Reads the user's daily log for an explicit local date.
    /// </summary>
    /// <param name="userId">The user whose daily log should be read.</param>
    /// <param name="date">The local date associated with the log file.</param>
    /// <param name="ct">A token that can cancel the read.</param>
    /// <returns>The stored daily log text, or <see langword="null"/> when no log exists.</returns>
    Task<string?> ReadDailyLogForDateAsync(string userId, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Appends an entry to the user's daily log for the current date in the requested time zone.
    /// </summary>
    /// <param name="userId">The user whose daily log should be updated.</param>
    /// <param name="logEntry">The log entry text to append.</param>
    /// <param name="timeZone">The time zone used to choose the daily log date, or UTC when omitted.</param>
    /// <param name="ct">A token that can cancel before the final mutation; an admitted write completes.</param>
    Task AppendDailyLogAsync(string userId, string logEntry, TimeZoneInfo? timeZone = null, CancellationToken ct = default);

    /// <summary>
    /// Lists daily log dates available for the user.
    /// </summary>
    /// <param name="userId">The user whose daily log dates should be listed.</param>
    /// <param name="ct">A token that can cancel the listing.</param>
    /// <returns>Date strings for persisted daily logs.</returns>
    Task<IReadOnlyList<string>> ListDailyLogsDatesAsync(string userId, CancellationToken ct = default);
}
