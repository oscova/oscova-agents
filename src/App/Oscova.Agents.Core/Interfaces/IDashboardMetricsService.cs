using Oscova.Agents.Core.ApiContracts.Dashboard;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// Builds the metrics dashboard snapshot for a single user.
/// </summary>
public interface IDashboardMetricsService
{
    /// <summary>
    /// Builds a read-only dashboard snapshot for one user's persisted data.
    /// </summary>
    Task<DashboardSnapshot> BuildAsync(string userId, DateTimeOffset since, bool isAdminView, CancellationToken ct = default);
}
