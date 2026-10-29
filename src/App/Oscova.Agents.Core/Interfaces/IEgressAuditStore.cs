using Oscova.Agents.Core.ApiContracts.Audit;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// Appends and reads the durable per-user audit log for real egress events.
/// </summary>
public interface IEgressAuditStore
{
    /// <summary>
    /// Appends one egress audit record for the user.
    /// </summary>
    Task AppendAsync(string userId, EgressAuditRecord record, CancellationToken ct = default);

    /// <summary>
    /// Appends several egress audit records under one file lock.
    /// </summary>
    Task AppendRangeAsync(string userId, IReadOnlyList<EgressAuditRecord> records, CancellationToken ct = default);

    /// <summary>
    /// Reads the complete per-user egress audit history.
    /// </summary>
    Task<IReadOnlyList<EgressAuditRecord>> ReadAllAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Reads one filtered page of egress audit history for a user.
    /// </summary>
    Task<EgressAuditPage> QueryAsync(string userId, EgressAuditQueryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Reads filtered egress audit records for a user without applying page slicing.
    /// </summary>
    Task<IReadOnlyList<EgressAuditRecord>> QueryRecordsAsync(string userId, EgressAuditQueryRequest request, CancellationToken ct = default);

    /// <summary>Rewrites every line of the user's file under the store's write gate; the function returns unparsable lines unchanged.</summary>
    Task RewriteLinesAsync(string userId, Func<string, string> rewriteLine, CancellationToken ct = default);
}
