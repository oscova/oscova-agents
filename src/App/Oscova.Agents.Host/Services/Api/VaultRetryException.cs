namespace Oscova.Agents.Host.Services.Api;

/// <summary>Preserves the server's vault cooldown for password forms.</summary>
public sealed class VaultRetryException(string message, DateTimeOffset retryUntil) : Exception(message)
{
    public DateTimeOffset RetryUntil { get; } = retryUntil;
}
