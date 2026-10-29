using Oscova.Agents.Core.ApiContracts.Dashboard;

namespace Oscova.Agents.Host.Achievements;

/// <summary>
/// Defines one achievement and how its progress is read from a dashboard snapshot.
/// </summary>
public sealed record AchievementDefinition(
    string Key,
    string Icon,
    int Required,
    int Points,
    Func<DashboardSnapshot, int> Selector);
