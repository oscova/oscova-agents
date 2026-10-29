using Oscova.Agents.Core.ApiContracts.Tools;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.ToolGovernance;

/// <summary>
/// Reserves and reads per-user tool quota usage outside the human-edited settings file.
/// </summary>
public interface IToolQuotaUsageStore
{
    Task<ToolQuotaDecision> TryReserveAsync(
        string userId,
        string qualifiedName,
        ToolCallLimit limit,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<ToolQuotaUsageSummary> GetUsageAsync(
        string userId,
        string qualifiedName,
        ToolCallLimit limit,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Moves usage from the plaintext file of earlier versions into the encrypted file, merging what is there.</summary>
    Task EncryptPlaintextAsync(string userId, CancellationToken ct = default);
}
