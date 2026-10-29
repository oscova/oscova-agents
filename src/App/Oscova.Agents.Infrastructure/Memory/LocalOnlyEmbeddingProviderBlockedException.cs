namespace Oscova.Agents.Infrastructure.Memory;

/// <summary>
/// Signals that local-only policy blocked a cloud embedding provider before credentials or generator creation.
/// </summary>
public sealed class LocalOnlyEmbeddingProviderBlockedException : InvalidOperationException
{
    public LocalOnlyEmbeddingProviderBlockedException(string providerId)
        : base(
            $"Offline mode is on: provider '{providerId}' uses a non-local endpoint and is blocked. " +
            "Use a local embedding model or point the provider at an approved local/LAN endpoint.")
    {
        ProviderId = providerId;
    }

    public string ProviderId { get; }
}
