using System.Collections.Concurrent;
using Oscova.Agents.Core.ApiContracts.Dashboard;

namespace Oscova.Agents.Host.Server.Services;

/// <summary>
/// Caches precomputed dashboard snapshots for warm request-path reads.
/// </summary>
public sealed class DashboardSnapshotCache
{
    private readonly ConcurrentDictionary<string, DashboardSnapshot> _perUser = new(StringComparer.Ordinal);
    private volatile AdminOverlay? _adminOverlay;

    public AdminOverlay? AdminOverlay
    {
        get => _adminOverlay;
        set => _adminOverlay = value;
    }

    /// <summary>
    /// Returns a user's cached snapshot when the background aggregator has populated it.
    /// </summary>
    public DashboardSnapshot? TryGet(string userId)
    {
        return _perUser.TryGetValue(userId, out var snapshot) ? snapshot : null;
    }

    /// <summary>
    /// Stores or replaces a user's cached snapshot.
    /// </summary>
    public void Set(string userId, DashboardSnapshot snapshot)
    {
        _perUser[userId] = snapshot;
    }

    /// <summary>
    /// Removes a user's cached snapshot.
    /// </summary>
    public void Remove(string userId)
    {
        _perUser.TryRemove(userId, out _);
    }
}

/// <summary>
/// Admin-only cross-user dashboard overlay cached by the metrics aggregator.
/// </summary>
public sealed record AdminOverlay(
    int UserCount,
    IReadOnlyList<PerUserUsageEntry> UsageByUser,
    DashboardSnapshot SystemWideUsage);
