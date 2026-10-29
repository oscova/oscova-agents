using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Infrastructure.Metrics;

/// <summary>
/// Builds dashboard snapshots by combining existing per-user configuration with metrics events.
/// </summary>
public sealed class DashboardMetricsService(
    UserServiceManager userServiceManager,
    IUserDirectoryService userDirectoryService,
    IMetricsEventStore metricsEventStore,
    IEgressAuditStore egressAuditStore,
    IAgentCatalog agentCatalog) : IDashboardMetricsService
{
    public async Task<DashboardSnapshot> BuildAsync(string userId, DateTimeOffset since, bool isAdminView, CancellationToken ct = default)
    {
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var events = await metricsEventStore.ReadAsync(userId, since, ct).ConfigureAwait(false);
        var egressRecords = (await egressAuditStore.ReadAllAsync(userId, ct).ConfigureAwait(false))
            .Where(record => record.Timestamp >= since)
            .ToList();
        var labels = UserLabelMap.Create(userScope, agentCatalog);
        var aggregation = Aggregate(events, egressRecords, labels);

        var skillCount = 0;
        var skillsDirectory = userDirectoryService.GetSkillsDirectoryPath(userId);
        if (Directory.Exists(skillsDirectory))
        {
            skillCount = new SkillDiscoverer().Discover(skillsDirectory).Count;
        }

        return aggregation with
        {
            AgentCount = 1 + userScope.AppService.CustomAgentDefinitions.Count,
            ModelProviderCount = userScope.ModelProviderSettings.ModelProviderConfigs.Count,
            TaskCount = (await userScope.AgentTaskRepository.GetTasksByUserAsync(userId, ct).ConfigureAwait(false)).Count,
            ToolCount = userScope.AppService.AllTools.Count,
            McpServerCount = userScope.McpToolSettings.McpServerConfigs.Count,
            SkillCount = skillCount,
            // Confidential threads are session-dependent, so stable metrics exclude them.
            ThreadCount = (await userScope.ChatThreadRepository.GetThreadsAsync(userId, ct).ConfigureAwait(false)).Count,
            IsAdminView = isAdminView,
            // The operator's system-wide view merges this instead, so it never sees another user's internals.
            SystemContribution = userScope.ModelProviderSettings.UsesOperatorCatalog
                ? Aggregate(events, egressRecords, labels.ForSystemView())
                : null
        };
    }

    // Rows are keyed by id so a rename keeps one row and equal names stay apart; collapsed system rows key by their bucket.
    private static DashboardSnapshot Aggregate(IReadOnlyList<MetricsEvent> events, IReadOnlyList<EgressAuditRecord> egressRecords, UserLabelMap labels)
    {
        var tokensByProvider = new Dictionary<string, TokenAccumulator>(StringComparer.OrdinalIgnoreCase);
        var tokensByAgent = new Dictionary<string, TokenAccumulator>(StringComparer.OrdinalIgnoreCase);
        var tokensByModel = new Dictionary<string, TokenAccumulator>(StringComparer.OrdinalIgnoreCase);
        var costByProvider = new Dictionary<string, CostAccumulator>(StringComparer.OrdinalIgnoreCase);
        var egressByProvider = new Dictionary<string, LabeledCount>(StringComparer.OrdinalIgnoreCase);
        var egressByAgent = new Dictionary<string, LabeledCount>(StringComparer.OrdinalIgnoreCase);
        var toolUsage = new Dictionary<string, ToolAccumulator>(StringComparer.OrdinalIgnoreCase);
        var taskRunsByTrigger = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var timeline = new Dictionary<DateOnly, TimelineAccumulator>();

        long totalInputTokens = 0;
        long totalOutputTokens = 0;
        long totalTokens = 0;
        decimal estimatedCost = 0;
        var hasUnpricedUsage = false;
        var toolInvocationCount = 0;
        var taskRunCount = 0;
        var taskSucceededCount = 0;
        var taskFailedCount = 0;
        var durationTotal = 0d;
        var durationCount = 0;
        var fallbackActivationCount = 0;

        foreach (var metricsEvent in events)
        {
            var date = DateOnly.FromDateTime(metricsEvent.Timestamp.UtcDateTime.Date);
            if (!timeline.TryGetValue(date, out var timePoint))
            {
                timePoint = new TimelineAccumulator();
                timeline[date] = timePoint;
            }

            foreach (var usage in metricsEvent.Usage)
            {
                totalInputTokens += usage.InputTokens;
                totalOutputTokens += usage.OutputTokens;
                totalTokens += usage.TotalTokens;
                timePoint.InputTokens += usage.InputTokens;
                timePoint.OutputTokens += usage.OutputTokens;
                timePoint.Tokens += usage.TotalTokens;

                var providerId = usage.ProviderId.ToString();
                var providerLabel = labels.Provider(providerId);
                var providerKey = ProviderRowKey(labels, providerId, providerLabel);
                AddTokens(tokensByProvider, providerKey, providerLabel, usage);
                var agentLabel = labels.Agent(usage.AgentId);
                AddTokens(tokensByAgent, AgentRowKey(labels, usage.AgentId, agentLabel), agentLabel, usage);
                var modelLabel = labels.Model(providerId, usage.ModelId);
                AddTokens(tokensByModel, modelLabel, modelLabel, usage);

                // Price recorded input and output buckets; reported residual tokens have no rate.
                var hasInputPrice = usage.InputTokens == 0 || usage.InputCostPerMillionTokens.HasValue;
                var hasOutputPrice = usage.OutputTokens == 0 || usage.OutputCostPerMillionTokens.HasValue;
                if (hasInputPrice && hasOutputPrice && (usage.InputTokens != 0 || usage.OutputTokens != 0))
                {
                    var cost = (usage.InputTokens * (usage.InputCostPerMillionTokens ?? 0m)
                        + usage.OutputTokens * (usage.OutputCostPerMillionTokens ?? 0m)) / 1_000_000m;
                    estimatedCost += cost;
                    if (!costByProvider.TryGetValue(providerKey, out var accumulator))
                    {
                        accumulator = new CostAccumulator { Label = providerLabel };
                        costByProvider[providerKey] = accumulator;
                    }

                    accumulator.TotalTokens += usage.TotalTokens;
                    accumulator.EstimatedCost += cost;
                }
                else if (usage.TotalTokens > 0 || usage.InputTokens != 0 || usage.OutputTokens != 0)
                {
                    hasUnpricedUsage = true;
                }

                // A missing report is incomplete coverage even when every recorded count is zero.
                if (usage.HasMissingUsage || usage.TotalTokens > usage.InputTokens + usage.OutputTokens)
                {
                    hasUnpricedUsage = true;
                }
            }

            foreach (var tool in metricsEvent.Tools)
            {
                var toolLabel = labels.Tool(tool.ToolName, tool.McpServerId, tool.AgentId);
                var toolKey = labels.IsSystemView ? toolLabel : $"{tool.ToolName}\u0000{tool.McpServerId}\u0000{tool.AgentId}";
                if (!toolUsage.TryGetValue(toolKey, out var accumulator))
                {
                    accumulator = new ToolAccumulator { Label = toolLabel };
                    toolUsage[toolKey] = accumulator;
                }

                accumulator.Succeeded += tool.Succeeded;
                accumulator.Failed += tool.Failed;
                toolInvocationCount += tool.Succeeded + tool.Failed;
            }

            if (metricsEvent.FallbackActivated)
            {
                fallbackActivationCount++;
            }

            // Skipped tasks did not execute, so they contribute no execution statistics.
            if (string.Equals(metricsEvent.Channel, "agent_task", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(metricsEvent.TaskStatus, "Skipped", StringComparison.OrdinalIgnoreCase))
            {
                taskRunCount++;
                if (string.Equals(metricsEvent.TaskStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    taskSucceededCount++;
                }
                else if (!string.IsNullOrWhiteSpace(metricsEvent.TaskStatus))
                {
                    taskFailedCount++;
                }

                if (metricsEvent.DurationSeconds is { } duration)
                {
                    durationTotal += duration;
                    durationCount++;
                }

                var trigger = string.IsNullOrWhiteSpace(metricsEvent.TriggerSource) ? "Unknown" : metricsEvent.TriggerSource;
                Increment(taskRunsByTrigger, trigger);
            }
        }

        foreach (var egress in egressRecords)
        {
            var date = DateOnly.FromDateTime(egress.Timestamp.UtcDateTime.Date);
            if (!timeline.TryGetValue(date, out var timePoint))
            {
                timePoint = new TimelineAccumulator();
                timeline[date] = timePoint;
            }

            timePoint.Egress++;
            var egressProvider = labels.Provider(egress.ProviderId);
            Increment(egressByProvider, ProviderRowKey(labels, egress.ProviderId, egressProvider), egressProvider);
            var egressAgent = labels.Agent(egress.AgentId);
            Increment(egressByAgent, AgentRowKey(labels, egress.AgentId, egressAgent), egressAgent);
        }

        return new DashboardSnapshot
        {
            TotalInputTokens = totalInputTokens,
            TotalOutputTokens = totalOutputTokens,
            TotalTokens = totalTokens,
            EstimatedCost = estimatedCost,
            HasUnpricedUsage = hasUnpricedUsage,
            CostByProvider = [.. costByProvider
                .OrderByDescending(pair => pair.Value.EstimatedCost)
                .Select(pair => new CostBreakdownEntry(pair.Value.Label, pair.Value.TotalTokens, pair.Value.EstimatedCost, pair.Key))],
            TokensByProvider = ToTokenBreakdown(tokensByProvider),
            TokensByAgent = ToTokenBreakdown(tokensByAgent),
            TokensByModel = ToTokenBreakdown(tokensByModel),
            EgressCount = egressRecords.Count,
            EgressByProvider = ToEgressBreakdown(egressByProvider),
            EgressByAgent = ToEgressBreakdown(egressByAgent),
            ToolInvocationCount = toolInvocationCount,
            ToolUsage = [.. toolUsage
                .OrderByDescending(pair => pair.Value.Succeeded + pair.Value.Failed)
                .Select(pair => new ToolUsageEntry(pair.Value.Label, pair.Value.Succeeded, pair.Value.Failed))],
            TaskRunCount = taskRunCount,
            TaskSucceededCount = taskSucceededCount,
            TaskFailedCount = taskFailedCount,
            TaskSuccessRate = taskRunCount == 0 ? 0 : (double)taskSucceededCount / taskRunCount,
            AverageTaskDurationSeconds = durationCount == 0 ? 0 : durationTotal / durationCount,
            TaskRunsByTrigger = [.. taskRunsByTrigger
                .OrderByDescending(pair => pair.Value)
                .Select(pair => new CountByLabel(pair.Key, pair.Value))],
            FallbackActivationCount = fallbackActivationCount,
            TokenTimeline = [.. timeline
                .OrderBy(pair => pair.Key)
                .Select(pair => new UsageTimePoint(
                    pair.Key,
                    pair.Value.Tokens,
                    0,
                    pair.Value.InputTokens,
                    pair.Value.OutputTokens))],
            EgressTimeline = [.. timeline
                .OrderBy(pair => pair.Key)
                .Select(pair => new UsageTimePoint(pair.Key, 0, pair.Value.Egress))]
        };
    }

    // A row the system view collapses into a bucket keys by that bucket, so every user's share merges into one row.
    private static string ProviderRowKey(UserLabelMap labels, string? id, string label) =>
        string.IsNullOrWhiteSpace(id) || (labels.IsSystemView && !labels.IsSharedProvider(id)) ? label : id;

    private static string AgentRowKey(UserLabelMap labels, string? id, string label) =>
        string.IsNullOrWhiteSpace(id) || labels.IsSystemView ? label : id;

    private static void AddTokens(Dictionary<string, TokenAccumulator> target, string key, string label, AgentProviderUsage usage)
    {
        if (!target.TryGetValue(key, out var accumulator))
        {
            accumulator = new TokenAccumulator { Label = label };
            target[key] = accumulator;
        }

        accumulator.InputTokens += usage.InputTokens;
        accumulator.OutputTokens += usage.OutputTokens;
        accumulator.TotalTokens += usage.TotalTokens;
    }

    private static IReadOnlyList<TokenBreakdownEntry> ToTokenBreakdown(Dictionary<string, TokenAccumulator> source)
    {
        return [.. source
            .OrderByDescending(pair => pair.Value.TotalTokens)
            .Select(pair => new TokenBreakdownEntry(
                pair.Value.Label,
                pair.Value.InputTokens,
                pair.Value.OutputTokens,
                pair.Value.TotalTokens,
                pair.Key))];
    }

    private static IReadOnlyList<EgressBreakdownEntry> ToEgressBreakdown(Dictionary<string, LabeledCount> source)
    {
        return [.. source
            .OrderByDescending(pair => pair.Value.Count)
            .Select(pair => new EgressBreakdownEntry(pair.Value.Label, pair.Value.Count, pair.Key))];
    }

    private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key)
        where TKey : notnull
    {
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static void Increment(Dictionary<string, LabeledCount> counts, string key, string label)
    {
        if (!counts.TryGetValue(key, out var entry))
        {
            entry = new LabeledCount { Label = label };
            counts[key] = entry;
        }

        entry.Count++;
    }

    private sealed class LabeledCount
    {
        public required string Label { get; init; }
        public int Count { get; set; }
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
        public required string Label { get; init; }
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
