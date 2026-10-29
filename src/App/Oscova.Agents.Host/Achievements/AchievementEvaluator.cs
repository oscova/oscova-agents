using Oscova.Agents.Core.ApiContracts.Achievements;
using Oscova.Agents.Core.ApiContracts.Dashboard;

namespace Oscova.Agents.Host.Achievements;

/// <summary>
/// Derives achievement progress, XP, and level from one dashboard snapshot.
/// </summary>
public static class AchievementEvaluator
{
    /// <summary>
    /// Evaluates the complete achievement catalog for the supplied snapshot.
    /// </summary>
    public static AchievementSummary Evaluate(DashboardSnapshot snapshot)
    {
        var achievements = AchievementCatalog.All
            .Select(definition => new AchievementProgress(
                definition.Key,
                definition.Icon,
                Math.Max(0, definition.Selector(snapshot)),
                definition.Required,
                definition.Points))
            .ToArray();
        var unlocked = achievements.Where(achievement => achievement.IsUnlocked).ToArray();
        var totalPoints = unlocked.Sum(achievement => achievement.Points);

        return new AchievementSummary(
            achievements,
            unlocked.Length,
            achievements.Length,
            totalPoints,
            AchievementCatalog.LevelFor(totalPoints));
    }
}
