using Microsoft.Extensions.AI;

namespace Oscova.Agents.Core.Interfaces
{
    /// <summary>
    /// Describes whether the selected embedding provider would send data to a cloud endpoint.
    /// </summary>
    public readonly record struct EmbeddingEgressInfo(bool IsCloud, string ProviderId, string? ModelId);

    /// <summary>Distinguishes an expected budget pause from an unconfigured embedding client.</summary>
    public sealed record EmbeddingClientResult(IEmbeddingGenerator<string, Embedding<float>>? Generator, string? UnavailableReason = null);

    public interface IEmbeddingGeneratorFactory
    {
        /// <summary>
        /// Creates an embedding generator from the current embedding provider settings.
        /// Returns null if embedding is not configured.
        /// </summary>
        Task<EmbeddingClientResult> CreateAsync(
            bool requireLocalEndpoint = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Describes the selected embedding provider without creating the generator.
        /// </summary>
        EmbeddingEgressInfo? DescribeEgress();
    }
}
