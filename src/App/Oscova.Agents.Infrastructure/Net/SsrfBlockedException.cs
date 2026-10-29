namespace Oscova.Agents.Infrastructure.Net;

/// <summary>
/// Signals that web__fetch refused a URL because its host resolves outside the allowed address policy.
/// </summary>
public sealed class SsrfBlockedException(string host)
    : Exception($"Host '{host}' resolves to an address web__fetch is not allowed to contact.")
{
    public string Host { get; } = host;
}
