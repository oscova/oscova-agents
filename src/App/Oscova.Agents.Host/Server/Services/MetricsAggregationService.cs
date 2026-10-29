using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Host.Server.Services;

/// <summary>
/// Continuously precomputes metrics dashboard snapshots for every user.
/// </summary>
public sealed class MetricsAggregationService(
    IServiceProvider serviceProvider,
    DashboardSnapshotCache cache,
    ILogger<MetricsAggregationService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);
    // Shell access can create links inside workspaces; storage accounting should not follow them outside the user root.
    private static readonly EnumerationOptions UserStorageEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("Metrics aggregation service started. Tick interval: {Interval}s", TickInterval.TotalSeconds);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            await RunTickAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunTickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("Metrics aggregation service stopped.");
    }

    private async Task RunTickAsync(CancellationToken ct)
    {
        try
        {
            await TickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error during metrics aggregation tick.");
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var scope = serviceProvider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var metrics = scope.ServiceProvider.GetRequiredService<IDashboardMetricsService>();
            var userDirectoryService = scope.ServiceProvider.GetRequiredService<IUserDirectoryService>();
            var contentProtector = scope.ServiceProvider.GetRequiredService<IContentProtector>();
            var since = DateTimeOffset.UtcNow.Subtract(Window);

            var users = await userManager.Users
                .Where(user => user.Id != WellKnownUsers.SystemId)
                .OrderBy(user => user.UserName)
                .ToListAsync(ct).ConfigureAwait(false);

            var perUserEntries = new List<PerUserUsageEntry>(users.Count);
            var snapshots = new List<DashboardSnapshot>(users.Count);

            foreach (var user in users)
            {
                ct.ThrowIfCancellationRequested();
                // Background snapshots wait for the user to unlock encrypted metrics.
                if (!contentProtector.IsAvailable(user.Id)) { continue; }

                try
                {
                    var snapshot = await metrics.BuildAsync(user.Id, since, isAdminView: false, ct).ConfigureAwait(false);
                    cache.Set(user.Id, snapshot);
                    snapshots.Add(snapshot);
                    var storageUsage = GetUserStorageUsage(userDirectoryService, user.Id);
                    perUserEntries.Add(new PerUserUsageEntry(
                        user.UserName ?? string.Empty,
                        snapshot.TotalTokens,
                        snapshot.EgressCount,
                        storageUsage.Bytes,
                        storageUsage.FileCount));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to build metrics dashboard snapshot for user {UserId}.", user.Id);
                }
            }

            cache.AdminOverlay = new AdminOverlay(perUserEntries.Count, perUserEntries, MergeSnapshots(snapshots));
        }
    }

    private (long Bytes, int FileCount) GetUserStorageUsage(IUserDirectoryService userDirectoryService, string userId)
    {
        try
        {
            var userDirectoryPath = userDirectoryService.GetUserDirectoryPath(userId);
            if (!Directory.Exists(userDirectoryPath))
            {
                return (0, 0);
            }

            var totalBytes = 0L;
            var fileCount = 0;
            foreach (var filePath in Directory.EnumerateFiles(userDirectoryPath, "*", UserStorageEnumerationOptions))
            {
                if (fileCount < int.MaxValue)
                {
                    fileCount++;
                }

                var fileBytes = new FileInfo(filePath).Length;
                if (long.MaxValue - totalBytes < fileBytes)
                {
                    return (long.MaxValue, fileCount);
                }

                totalBytes += fileBytes;
            }

            return (totalBytes, fileCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to calculate user storage size for user {UserId}.", userId);
            return (0, 0);
        }
    }

    private static DashboardSnapshot MergeSnapshots(IReadOnlyList<DashboardSnapshot> snapshots)
    {
        var tokensByProvider = new Dictionary<string, TokenAccumulator>(StringComparer.OrdinalIgnoreCase);
        var tokensByAgent = new Dictionary<string, TokenAccumulator>(StringComparer.OrdinalIgnoreCase);
        var tokensByModel = new Dictionary<string, TokenAccumulator>(StringComparer.OrdinalIgnoreCase);
        var costByProvider = new Dictionary<string, CostAccumulator>(StringComparer.OrdinalIgnoreCase);
        var egressByProvider = new Dictionary<string, EgressBreakdownEntry>(StringComparer.OrdinalIgnoreCase);
        var egressByAgent = new Dictionary<string, EgressBreakdownEntry>(StringComparer.OrdinalIgnoreCase);
        var toolUsage = new Dictionary<string, ToolAccumulator>(StringComparer.OrdinalIgnoreCase);
        var taskRunsByTrigger = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tokenTimeline = new Dictionary<DateOnly, TimelineAccumulator>();
        var egressTimeline = new Dictionary<DateOnly, TimelineAccumulator>();

        var result = new DashboardSnapshot();
        foreach (var snapshot in snapshots)
        {
            result = result with
            {
                AgentCount = result.AgentCount + snapshot.AgentCount,
                ModelProviderCount = result.ModelProviderCount + snapshot.ModelProviderCount,
                TaskCount = result.TaskCount + snapshot.TaskCount,
                ToolCount = result.ToolCount + snapshot.ToolCount,
                McpServerCount = result.McpServerCount + snapshot.McpServerCount,
                SkillCount = result.SkillCount + snapshot.SkillCount,
                ThreadCount = result.ThreadCount + snapshot.ThreadCount,
                TotalInputTokens = result.TotalInputTokens + snapshot.TotalInputTokens,
                TotalOutputTokens = result.TotalOutputTokens + snapshot.TotalOutputTokens,
                TotalTokens = result.TotalTokens + snapshot.TotalTokens,
                EstimatedCost = result.EstimatedCost + snapshot.EstimatedCost,
                HasUnpricedUsage = result.HasUnpricedUsage || snapshot.HasUnpricedUsage,
                EgressCount = result.EgressCount + snapshot.EgressCount,
                ToolInvocationCount = result.ToolInvocationCount + snapshot.ToolInvocationCount,
                TaskRunCount = result.TaskRunCount + snapshot.TaskRunCount,
                TaskSucceededCount = result.TaskSucceededCount + snapshot.TaskSucceededCount,
                TaskFailedCount = result.TaskFailedCount + snapshot.TaskFailedCount,
                FallbackActivationCount = result.FallbackActivationCount + snapshot.FallbackActivationCount
            };

            // Other users' rows arrive already collapsed to what the operator may see.
            var view = snapshot.SystemContribution ?? snapshot;
            AddTokens(tokensByProvider, view.TokensByProvider);
            AddTokens(tokensByAgent, view.TokensByAgent);
            AddTokens(tokensByModel, view.TokensByModel);
            AddCosts(costByProvider, view.CostByProvider);
            AddEgress(egressByProvider, view.EgressByProvider);
            AddEgress(egressByAgent, view.EgressByAgent);
            AddTools(toolUsage, view.ToolUsage);
            AddCounts(taskRunsByTrigger, snapshot.TaskRunsByTrigger);
            AddTimeline(tokenTimeline, snapshot.TokenTimeline);
            AddTimeline(egressTimeline, snapshot.EgressTimeline);
        }

        var durationTotal = 0d;
        var durationWeight = 0;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.AverageTaskDurationSeconds > 0 && snapshot.TaskRunCount > 0)
            {
                durationTotal += snapshot.AverageTaskDurationSeconds * snapshot.TaskRunCount;
                durationWeight += snapshot.TaskRunCount;
            }
        }

        return result with
        {
            TaskSuccessRate = result.TaskRunCount == 0 ? 0 : (double)result.TaskSucceededCount / result.TaskRunCount,
            AverageTaskDurationSeconds = durationWeight == 0 ? 0 : durationTotal / durationWeight,
            TokensByProvider = ToTokenBreakdown(tokensByProvider),
            TokensByAgent = ToTokenBreakdown(tokensByAgent),
            TokensByModel = ToTokenBreakdown(tokensByModel),
            CostByProvider = [.. costByProvider
                .OrderByDescending(pair => pair.Value.EstimatedCost)
                .Select(pair => new CostBreakdownEntry(pair.Value.Label, pair.Value.TotalTokens, pair.Value.EstimatedCost, pair.Key))],
            EgressByProvider = ToEgressBreakdown(egressByProvider),
            EgressByAgent = ToEgressBreakdown(egressByAgent),
            ToolUsage = [.. toolUsage
                .OrderByDescending(pair => pair.Value.Succeeded + pair.Value.Failed)
                .Select(pair => new ToolUsageEntry(pair.Key, pair.Value.Succeeded, pair.Value.Failed))],
            TaskRunsByTrigger = [.. taskRunsByTrigger
                .OrderByDescending(pair => pair.Value)
                .Select(pair => new CountByLabel(pair.Key, pair.Value))],
            TokenTimeline = [.. tokenTimeline
                .OrderBy(pair => pair.Key)
                .Select(pair => new UsageTimePoint(
                    pair.Key,
                    pair.Value.Tokens,
                    0,
                    pair.Value.InputTokens,
                    pair.Value.OutputTokens))],
            EgressTimeline = [.. egressTimeline
                .OrderBy(pair => pair.Key)
                .Select(pair => new UsageTimePoint(pair.Key, 0, pair.Value.Egress))]
        };
    }

    private static void AddTokens(Dictionary<string, TokenAccumulator> target, IReadOnlyList<TokenBreakdownEntry> entries)
    {
        foreach (var entry in entries)
        {
            var key = entry.Key ?? entry.Name;
            if (!target.TryGetValue(key, out var accumulator))
            {
                accumulator = new TokenAccumulator { Label = entry.Name };
                target[key] = accumulator;
            }

            accumulator.InputTokens += entry.InputTokens;
            accumulator.OutputTokens += entry.OutputTokens;
            accumulator.TotalTokens += entry.TotalTokens;
        }
    }

    private static void AddCosts(Dictionary<string, CostAccumulator> target, IReadOnlyList<CostBreakdownEntry> entries)
    {
        foreach (var entry in entries)
        {
            var key = entry.Key ?? entry.Name;
            if (!target.TryGetValue(key, out var accumulator))
            {
                accumulator = new CostAccumulator { Label = entry.Name };
                target[key] = accumulator;
            }

            accumulator.TotalTokens += entry.TotalTokens;
            accumulator.EstimatedCost += entry.EstimatedCost;
        }
    }

    private static void AddEgress(Dictionary<string, EgressBreakdownEntry> target, IReadOnlyList<EgressBreakdownEntry> entries)
    {
        foreach (var entry in entries)
        {
            var key = entry.Key ?? entry.Name;
            target[key] = target.TryGetValue(key, out var existing) ? existing with { Count = existing.Count + entry.Count } : entry;
        }
    }

    private static void AddTools(Dictionary<string, ToolAccumulator> target, IReadOnlyList<ToolUsageEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!target.TryGetValue(entry.ToolName, out var accumulator))
            {
                accumulator = new ToolAccumulator();
                target[entry.ToolName] = accumulator;
            }

            accumulator.Succeeded += entry.Succeeded;
            accumulator.Failed += entry.Failed;
        }
    }

    private static void AddCounts(Dictionary<string, int> target, IReadOnlyList<CountByLabel> entries)
    {
        foreach (var entry in entries)
        {
            target[entry.Label] = target.TryGetValue(entry.Label, out var count) ? count + entry.Count : entry.Count;
        }
    }

    private static void AddTimeline(Dictionary<DateOnly, TimelineAccumulator> target, IReadOnlyList<UsageTimePoint> entries)
    {
        foreach (var entry in entries)
        {
            if (!target.TryGetValue(entry.Date, out var accumulator))
            {
                accumulator = new TimelineAccumulator();
                target[entry.Date] = accumulator;
            }

            accumulator.Tokens += entry.Tokens;
            accumulator.InputTokens += entry.InputTokens;
            accumulator.OutputTokens += entry.OutputTokens;
            accumulator.Egress += entry.Egress;
        }
    }

    private static IReadOnlyList<TokenBreakdownEntry> ToTokenBreakdown(Dictionary<string, TokenAccumulator> source)
    {
        return [.. source
            .OrderByDescending(pair => pair.Value.TotalTokens)
            .Select(pair => new TokenBreakdownEntry(pair.Value.Label, pair.Value.InputTokens, pair.Value.OutputTokens, pair.Value.TotalTokens, pair.Key))];
    }

    private static IReadOnlyList<EgressBreakdownEntry> ToEgressBreakdown(Dictionary<string, EgressBreakdownEntry> source)
    {
        return [.. source.Values.OrderByDescending(entry => entry.Count)];
    }

    private sealed class TokenAccumulator
    {
        public required string Label { get; init; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens { get; set; }
    }

    private sealed class CostAccumulator
    {
        public required string Label { get; init; }
        public long TotalTokens { get; set; }
        public decimal EstimatedCost { get; set; }
    }

    private sealed class ToolAccumulator
    {
        public int Succeeded { get; set; }
        public int Failed { get; set; }
    }

    private sealed class TimelineAccumulator
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long Tokens { get; set; }
        public int Egress { get; set; }
    }
}
