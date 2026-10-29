using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Oscova.Agents.Infrastructure.OAuth;

/// <summary>Binds one-use consent state to its initiating user and provider.</summary>
public sealed class OAuthStateStore
{
    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);

    /// <summary>Creates consent state valid for ten minutes.</summary>
    public string Generate(string userId, string providerId, out string codeChallenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        _store[state] = new Entry(new Initiator(userId, providerId, verifier), DateTimeOffset.UtcNow.AddMinutes(10));
        PurgeExpired();
        return state;
    }

    /// <summary>Removes state and returns its initiating identity when it has not expired.</summary>
    public bool TryConsume(string state, out Initiator initiated)
    {
        initiated = new Initiator(string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        if (!_store.TryRemove(state, out var entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            return false;
        }

        initiated = entry.Initiated;
        return true;
    }

    // OAuth uses the URL-safe alphabet without base64 padding.
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _store)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _store.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>Binds the initiating identity and PKCE verifier to one consent attempt.</summary>
    public sealed record Initiator(string UserId, string ProviderId, string CodeVerifier);

    private sealed record Entry(Initiator Initiated, DateTimeOffset ExpiresAt);
}
