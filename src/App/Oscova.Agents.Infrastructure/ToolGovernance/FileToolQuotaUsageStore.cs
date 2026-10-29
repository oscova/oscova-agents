using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Secrets;
using Oscova.Agents.Utils;
using Oscova.Agents.Core.ApiContracts.Tools;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.ToolGovernance;

namespace Oscova.Agents.Infrastructure.ToolGovernance;

/// <summary>
/// Persists compact per-user tool quota usage state, encrypted in the user's app-data directory.
/// </summary>
/// <remarks>Usage is keyed by qualified tool names, which carry MCP server names, so the file is vault-encrypted.</remarks>
public sealed class FileToolQuotaUsageStore(
    IUserDirectoryService userDirectoryService,
    IContentProtector protector,
    ILogger<FileToolQuotaUsageStore> logger) : IToolQuotaUsageStore
{
    private const string StateDirectoryName = "tool-governance";
    private const string UsageFileName = "usage.json.enc";
    private const string PlaintextUsageFileName = "usage.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ToolQuotaDecision> TryReserveAsync(
        string userId,
        string qualifiedName,
        ToolCallLimit limit,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ValidateLimit(limit);
        var normalizedName = NormalizeQualifiedName(qualifiedName);
        var path = GetUsagePath(userId);
        var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(userId, path, ct).ConfigureAwait(false);
            var entry = GetCurrentEntry(state, normalizedName, limit, now);
            if (entry.Used >= limit.MaxCalls)
            {
                return ToolQuotaDecision.Denied(
                    entry.Used,
                    limit.MaxCalls,
                    GetResetAt(entry, limit));
            }

            entry.Used++;
            entry.LastReservedAtUtc = now;
            state.Tools[normalizedName] = entry;
            await WriteStateAsync(userId, path, state, ct).ConfigureAwait(false);

            return ToolQuotaDecision.Allowed(
                entry.Used,
                limit.MaxCalls,
                GetResetAt(entry, limit));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ToolQuotaUsageSummary> GetUsageAsync(
        string userId,
        string qualifiedName,
        ToolCallLimit limit,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ValidateLimit(limit);
        var normalizedName = NormalizeQualifiedName(qualifiedName);
        var path = GetUsagePath(userId);
        var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(userId, path, ct).ConfigureAwait(false);
            var entry = GetCurrentEntry(state, normalizedName, limit, now);
            return ToSummary(entry, limit);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task EncryptPlaintextAsync(string userId, CancellationToken ct = default)
    {
        var path = GetUsagePath(userId);
        var plaintextPath = GetPlaintextPath(path);
        var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(plaintextPath)) { return; }

            var plaintext = Deserialize(await FileUtil.ReadAllTextSharedAsync(plaintextPath, ct).ConfigureAwait(false), plaintextPath);
            var state = await ReadStateAsync(userId, path, ct).ConfigureAwait(false);

            // Usage in either file counts: the later window wins, then the higher count within one window.
            foreach (var (name, entry) in plaintext.Tools)
            {
                if (!state.Tools.TryGetValue(name, out var existing) ||
                    entry.WindowStartedAtUtc > existing.WindowStartedAtUtc ||
                    (entry.WindowStartedAtUtc == existing.WindowStartedAtUtc && entry.Used > existing.Used))
                {
                    state.Tools[name] = entry;
                }
            }

            await WriteStateAsync(userId, path, state, ct).ConfigureAwait(false);
            File.Delete(plaintextPath);
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetUsagePath(string userId)
    {
        var userDirectory = userDirectoryService.GetUserDirectoryPath(userId);
        return Path.Combine(userDirectory, StateDirectoryName, UsageFileName);
    }

    private async Task<UsageState> ReadStateAsync(string userId, string path, CancellationToken ct)
    {
        var json = await EncryptedFileContent.ReadAllTextAsync(userId, path, protector, ct).ConfigureAwait(false);
        if (json is not null) { return Deserialize(json, path); }

        // Until the unlock migration encrypts it, the legacy plaintext file holds the live counts.
        var plaintextPath = GetPlaintextPath(path);
        return File.Exists(plaintextPath)
            ? Deserialize(await FileUtil.ReadAllTextSharedAsync(plaintextPath, ct).ConfigureAwait(false), plaintextPath)
            : new UsageState();
    }

    private static string GetPlaintextPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, PlaintextUsageFileName);

    private UsageState Deserialize(string json, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<UsageState>(json, JsonOptions) ?? new UsageState();
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Tool quota usage state at '{Path}' is malformed.", path);
            throw;
        }
    }

    private Task WriteStateAsync(string userId, string path, UsageState state, CancellationToken ct) =>
        EncryptedFileContent.WriteAllTextAsync(userId, path, JsonSerializer.Serialize(state, JsonOptions), protector, ct);

    private static UsageEntry GetCurrentEntry(
        UsageState state,
        string qualifiedName,
        ToolCallLimit limit,
        DateTimeOffset now)
    {
        if (!state.Tools.TryGetValue(qualifiedName, out var entry) ||
            now >= GetResetAt(entry, limit))
        {
            entry = new UsageEntry
            {
                WindowStartedAtUtc = now
            };
        }

        return entry;
    }

    private static ToolQuotaUsageSummary ToSummary(UsageEntry entry, ToolCallLimit limit) => new()
    {
        Used = entry.Used,
        MaxCalls = limit.MaxCalls,
        Remaining = Math.Max(0, limit.MaxCalls - entry.Used),
        WindowStartedAtUtc = entry.WindowStartedAtUtc,
        ResetsAtUtc = GetResetAt(entry, limit)
    };

    private static DateTimeOffset GetResetAt(UsageEntry entry, ToolCallLimit limit)
        => entry.WindowStartedAtUtc.AddHours(limit.WindowHours);

    private static string NormalizeQualifiedName(string qualifiedName)
    {
        var normalizedName = ToolNaming.NormalizeQualifier(qualifiedName);
        return string.IsNullOrWhiteSpace(normalizedName)
            ? throw new ArgumentException("A qualified tool name is required.", nameof(qualifiedName))
            : normalizedName;
    }

    private static void ValidateLimit(ToolCallLimit limit)
    {
        if (!limit.IsEnabled)
        {
            throw new ArgumentException("A quota limit must be enabled before usage can be reserved.", nameof(limit));
        }

        if (limit.MaxCalls is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit.MaxCalls, "Max calls must be between 1 and 1,000,000.");
        }

        if (limit.WindowHours != 24)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit.WindowHours, "Only a 24-hour window is supported.");
        }
    }

    private sealed class UsageState
    {
        public Dictionary<string, UsageEntry> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class UsageEntry
    {
        public DateTimeOffset WindowStartedAtUtc { get; set; }
        public int Used { get; set; }
        public DateTimeOffset? LastReservedAtUtc { get; set; }
    }
}
