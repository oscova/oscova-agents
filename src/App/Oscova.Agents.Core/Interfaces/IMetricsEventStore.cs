using Oscova.Agents.Core.ApiContracts.Dashboard;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// Persists and reads append-only per-user metrics events.
/// </summary>
public interface IMetricsEventStore
{
    /// <summary>
    /// Appends a metrics event for the specified user.
    /// </summary>
    Task AppendAsync(string userId, MetricsEvent metricsEvent, CancellationToken ct = default);

    /// <summary>
    /// Reads metrics events for the specified user since the supplied timestamp.
    /// </summary>
    Task<IReadOnlyList<MetricsEvent>> ReadAsync(string userId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Rewrites every line of the user's file under the store's write gate; the function returns unparsable lines unchanged.</summary>
    Task RewriteLinesAsync(string userId, Func<string, string> rewriteLine, CancellationToken ct = default);
}
