using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.Infrastructure.Metrics;

/// <summary>
/// File-backed append-only metrics event store.
/// </summary>
public sealed class MetricsEventStore(ILogger<MetricsEventStore> logger) : IMetricsEventStore
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(90);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPrunedUtc = new(StringComparer.OrdinalIgnoreCase);

    public async Task AppendAsync(string userId, MetricsEvent metricsEvent, CancellationToken ct = default)
    {
        var path = GetEventsPath(userId);
        var json = JsonSerializer.Serialize(metricsEvent, JsonOptions);
        var fileGate = _fileGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FileUtil.AppendLineSharedAsync(path, json, ct).ConfigureAwait(false);
            await PruneIfDueAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            fileGate.Release();
        }
    }

    public async Task RewriteLinesAsync(string userId, Func<string, string> rewriteLine, CancellationToken ct = default)
    {
        var path = GetEventsPath(userId);
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

    public Task<IReadOnlyList<MetricsEvent>> ReadAsync(string userId, DateTimeOffset since, CancellationToken ct = default)
    {
        var path = GetEventsPath(userId);
        if (!File.Exists(path))
        {
            return Task.FromResult<IReadOnlyList<MetricsEvent>>([]);
        }

        var result = new List<MetricsEvent>();
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
                var metricsEvent = JsonSerializer.Deserialize<MetricsEvent>(lines[index], JsonOptions);
                if (metricsEvent is not null && metricsEvent.Timestamp >= since)
                {
                    result.Add(metricsEvent);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping malformed metrics event line {LineNumber} in '{MetricsPath}'.", index + 1, path);
            }
        }

        return Task.FromResult<IReadOnlyList<MetricsEvent>>(result);
    }

    private async Task PruneIfDueAsync(string path, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastPrunedUtc.TryGetValue(path, out var lastPruned) &&
            now - lastPruned < PruneInterval)
        {
            return;
        }

        _lastPrunedUtc[path] = now;
        var cutoff = now.Subtract(RetentionWindow);
        var lines = FileUtil.ReadAllLinesShared(path);
        var retainedLines = new List<string>(lines.Length);
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                changed = true;
                continue;
            }

            try
            {
                var metricsEvent = JsonSerializer.Deserialize<MetricsEvent>(lines[index], JsonOptions);
                if (metricsEvent is null || metricsEvent.Timestamp < cutoff)
                {
                    changed = true;
                    continue;
                }

                retainedLines.Add(lines[index]);
            }
            catch (JsonException ex)
            {
                changed = true;
                logger.LogWarning(ex, "Pruned malformed metrics event line {LineNumber} in '{MetricsPath}'.", index + 1, path);
            }
        }

        if (!changed)
        {
            return;
        }

        var content = retainedLines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, retainedLines) + Environment.NewLine;
        await FileUtil.WriteAllTextAtomicAsync(path, content, ct).ConfigureAwait(false);
    }

    private static string GetEventsPath(string userId)
    {
        return Path.Combine(
            PathUtil.UsersPath,
            userId,
            UserFileNames.MetricsDirectory,
            UserFileNames.MetricsEventsFile);
    }
}
