using Oscova.Agents.Core.ApiContracts.Dashboard;

namespace Oscova.Agents.Host.Achievements;

/// <summary>
/// Owns the fixed achievement catalog and level thresholds.
/// </summary>
public static class AchievementCatalog
{
    public static readonly IReadOnlyList<AchievementDefinition> All =
    [
        new("Achievement.FirstThread", "lucide:message-circle", 1, 10, snapshot => snapshot.ThreadCount),
        new("Achievement.TenThreads", "lucide:messages-square", 10, 25, snapshot => snapshot.ThreadCount),
        new("Achievement.FiftyThreads", "lucide:library", 50, 50, snapshot => snapshot.ThreadCount),
        new("Achievement.FirstAgent", "lucide:bot", 1, 10, snapshot => snapshot.AgentCount - 1),
        new("Achievement.ThreeAgents", "lucide:users", 3, 25, snapshot => snapshot.AgentCount - 1),
        new("Achievement.FiveAgents", "lucide:users-round", 5, 50, snapshot => snapshot.AgentCount - 1),
        new("Achievement.TenAgents", "lucide:building-2", 10, 100, snapshot => snapshot.AgentCount - 1),
        new("Achievement.FirstProvider", "lucide:plug", 1, 10, snapshot => snapshot.ModelProviderCount),
        new("Achievement.ThreeProviders", "lucide:server", 3, 25, snapshot => snapshot.ModelProviderCount),
        new("Achievement.FiveProviders", "lucide:layers", 5, 50, snapshot => snapshot.ModelProviderCount),
        new("Achievement.FirstTask", "lucide:list-checks", 1, 10, snapshot => snapshot.TaskCount),
        new("Achievement.FiveTasks", "lucide:calendar-clock", 5, 25, snapshot => snapshot.TaskCount),
        new("Achievement.FifteenTasks", "lucide:workflow", 15, 50, snapshot => snapshot.TaskCount),
        new("Achievement.FirstTaskRun", "lucide:circle-check", 1, 10, snapshot => snapshot.TaskSucceededCount),
        new("Achievement.TwentyFiveTaskRuns", "lucide:check-check", 25, 25, snapshot => snapshot.TaskSucceededCount),
        new("Achievement.HundredTaskRuns", "lucide:trophy", 100, 50, snapshot => snapshot.TaskSucceededCount),
        new("Achievement.FirstScheduledRun", "lucide:alarm-clock", 1, 10, ScheduledRuns),
        new("Achievement.TenScheduledRuns", "lucide:timer", 10, 25, ScheduledRuns),
        new("Achievement.FirstMcpServer", "lucide:cable", 1, 10, snapshot => snapshot.McpServerCount),
        new("Achievement.ThreeMcpServers", "lucide:network", 3, 25, snapshot => snapshot.McpServerCount),
        new("Achievement.FiveMcpServers", "lucide:blocks", 5, 50, snapshot => snapshot.McpServerCount),
        new("Achievement.FirstSkill", "lucide:graduation-cap", 1, 10, snapshot => snapshot.SkillCount),
        new("Achievement.FiveSkills", "lucide:book-open", 5, 25, snapshot => snapshot.SkillCount),
        new("Achievement.TenSkills", "lucide:brain", 10, 50, snapshot => snapshot.SkillCount),
        new("Achievement.FiveTools", "lucide:wrench", 5, 10, snapshot => snapshot.ToolUsage.Count),
        new("Achievement.FifteenTools", "lucide:hammer", 15, 25, snapshot => snapshot.ToolUsage.Count),
        new("Achievement.ThirtyTools", "lucide:package-open", 30, 50, snapshot => snapshot.ToolUsage.Count),
        new("Achievement.FallbackSaved", "lucide:life-buoy", 1, 20, snapshot => snapshot.FallbackActivationCount),
        new("Achievement.FirstLocalDay", "lucide:shield", 1, 15, LocalOnlyDays),
        new("Achievement.SevenLocalDays", "lucide:shield-check", 7, 40, LocalOnlyDays),
        new("Achievement.ThirtyLocalDays", "lucide:lock", 30, 100, LocalOnlyDays)
    ];

    private static readonly int[] LevelThresholds = [0, 50, 150, 300, 500, 750];

    /// <summary>
    /// Returns the highest level reached by the supplied XP total.
    /// </summary>
    public static int LevelFor(int totalPoints)
    {
        var level = 1;
        for (var index = 1; index < LevelThresholds.Length; index++)
        {
            if (totalPoints < LevelThresholds[index])
            {
                break;
            }

            level = index + 1;
        }

        return level;
    }

    // Count only task runs started by the scheduler.
    private static int ScheduledRuns(DashboardSnapshot snapshot)
    {
        foreach (var entry in snapshot.TaskRunsByTrigger)
        {
            if (string.Equals(entry.Label, "Schedule", StringComparison.OrdinalIgnoreCase))
            {
                return entry.Count;
            }
        }

        return 0;
    }

    // A local-only day has model usage and no audited egress.
    private static int LocalOnlyDays(DashboardSnapshot snapshot)
    {
        var egressDates = snapshot.EgressTimeline
            .Where(point => point.Egress > 0)
            .Select(point => point.Date)
            .ToHashSet();

        return snapshot.TokenTimeline.Count(point => point.Tokens > 0 && !egressDates.Contains(point.Date));
    }
}
