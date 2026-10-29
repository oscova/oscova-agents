using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.Infrastructure.Audit;

/// <summary>
/// File-backed append-only store for durable per-user egress audit records.
/// </summary>
public sealed class EgressAuditStore(ILogger<EgressAuditStore> logger) : IEgressAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileGates = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendAsync(string userId, EgressAuditRecord record, CancellationToken ct = default)
    {
        return AppendRangeAsync(userId, [record], ct);
    }

    public async Task AppendRangeAsync(string userId, IReadOnlyList<EgressAuditRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
        {
            return;
        }

        var path = GetEgressLogPath(userId);
        var fileGate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var record in records)
            {
                var json = JsonSerializer.Serialize(record, JsonOptions);
                await FileUtil.AppendLineSharedAsync(path, json, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            fileGate.Release();
        }
    }

    public async Task RewriteLinesAsync(string userId, Func<string, string> rewriteLine, CancellationToken ct = default)
    {
        var path = GetEgressLogPath(userId);
        var fileGate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) { return; }

            var lines = FileUtil.ReadAllLinesShared(path).Select(line => string.IsNullOrWhiteSpace(line) ? line : rewriteLine(line));
            await FileUtil.WriteAllTextAtomicAsync(path, string.Concat(lines.Select(line => line + Environment.NewLine)), ct).ConfigureAwait(false);
        }
        finally
        {
            fileGate.Release();
        }
    }

    public Task<IReadOnlyList<EgressAuditRecord>> ReadAllAsync(string userId, CancellationToken ct = default)
    {
        var path = GetEgressLogPath(userId);
        if (!File.Exists(path))
        {
            return Task.FromResult<IReadOnlyList<EgressAuditRecord>>([]);
        }

        var result = new List<EgressAuditRecord>();
        var lines = FileUtil.ReadAllLinesShared(path);
        for (var index = 0; index < lines.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<EgressAuditRecord>(lines[index], JsonOptions);
                if (record is not null)
                {
                    result.Add(record);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping malformed egress audit line {LineNumber} in '{EgressAuditPath}'.", index + 1, path);
            }
        }

        return Task.FromResult<IReadOnlyList<EgressAuditRecord>>(result);
    }

    public async Task<EgressAuditPage> QueryAsync(string userId, EgressAuditQueryRequest request, CancellationToken ct = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var filtered = await QueryRecordsAsync(userId, request, ct).ConfigureAwait(false);

        return new EgressAuditPage
        {
            Entries = [.. filtered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(record => new EgressAuditEntry { Record = record })],
            CurrentPage = page,
            PageSize = pageSize,
            TotalCount = filtered.Count,
            TotalPages = TotalPages(filtered.Count, pageSize),
            Summary = BuildSummary(filtered)
        };
    }

    public async Task<IReadOnlyList<EgressAuditRecord>> QueryRecordsAsync(string userId, EgressAuditQueryRequest request, CancellationToken ct = default)
    {
        var records = await ReadAllAsync(userId, ct).ConfigureAwait(false);
        return [.. records
            .Where(record => Matches(record, request))
            .OrderByDescending(record => record.Timestamp)];
    }

    private static string GetEgressLogPath(string userId)
    {
        return Path.Combine(
            PathUtil.UsersPath,
            userId,
            UserFileNames.AuditDirectory,
            UserFileNames.EgressLogFile);
    }

    private static bool Matches(EgressAuditRecord record, EgressAuditQueryRequest request)
    {
        if (request.FromUtc is { } fromUtc && record.Timestamp < fromUtc)
        {
            return false;
        }

        if (request.ToUtc is { } toUtc && record.Timestamp > toUtc)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Kind) &&
            !string.Equals(record.Kind, request.Kind.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Source) &&
            !string.Equals(record.Source, request.Source.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesSearch(record, request.SearchTerm, request.SearchIds);
    }

    // Records hold ids, so labels are searched through the ids the host resolved from them.
    private static bool MatchesSearch(EgressAuditRecord record, string? searchTerm, IReadOnlyCollection<string> searchIds)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return true;
        }

        var term = searchTerm.Trim();
        return Contains(record.ThreadId, term) ||
               Contains(record.RunId, term) ||
               Contains(record.TaskId, term) ||
               Contains(record.AgentId, term) ||
               Contains(record.ProviderId, term) ||
               Contains(record.McpServerId, term) ||
               Contains(record.ModelId, term) ||
               Contains(record.ToolName, term) ||
               Contains(record.Kind, term) ||
               Contains(record.Source, term) ||
               IsAny(record.AgentId, searchIds) ||
               IsAny(record.ProviderId, searchIds) ||
               IsAny(record.McpServerId, searchIds);
    }

    private static bool IsAny(string? id, IReadOnlyCollection<string> ids) =>
        id is not null && ids.Contains(id, StringComparer.OrdinalIgnoreCase);

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static EgressAuditSummary BuildSummary(IReadOnlyCollection<EgressAuditRecord> records)
    {
        var providerCount = records.Count(record => string.Equals(record.Kind, EgressAuditKinds.Provider, StringComparison.OrdinalIgnoreCase));
        var toolCount = records.Count(record => string.Equals(record.Kind, EgressAuditKinds.Tool, StringComparison.OrdinalIgnoreCase));
        var embeddingCount = records.Count(record => string.Equals(record.Kind, EgressAuditKinds.Embedding, StringComparison.OrdinalIgnoreCase));

        return new EgressAuditSummary(records.Count, providerCount, toolCount, embeddingCount);
    }

    private static int TotalPages(int totalCount, int pageSize) =>
        Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
}
