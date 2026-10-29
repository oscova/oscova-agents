namespace Oscova.Agents.Core.ApiContracts.Achievements;

/// <summary>
/// One achievement with the viewer's current progress toward it.
/// </summary>
public sealed record AchievementProgress(string Key, string Icon, int Current, int Required, int Points)
{
    public bool IsUnlocked => Current >= Required;
}

/// <summary>
/// The full achievement wall for one user, including derived XP and level.
/// </summary>
public sealed record AchievementSummary(
    IReadOnlyList<AchievementProgress> Achievements,
    int UnlockedCount,
    int TotalCount,
    int TotalPoints,
    int Level);
