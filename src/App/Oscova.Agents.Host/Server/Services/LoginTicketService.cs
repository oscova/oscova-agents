using Microsoft.Extensions.Caching.Memory;

namespace Oscova.Agents.Host.Server.Services;

public sealed class LoginTicketService(IMemoryCache cache)
{
    private readonly IMemoryCache _cache = cache;
    private readonly Lock _consumeLock = new();
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private const string PasswordKind = "password";
    private const string PinKind = "pin";

    public string Create(string username, string password, TimeSpan? lifetime = null)
    {
        var token = Guid.NewGuid().ToString("N");
        _cache.Set(token, new Ticket(PasswordKind, username, password, null), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime ?? DefaultLifetime
        });
        return token;
    }

    /// <summary>Carries an entered PIN to fresh server-side verification without putting it in a URL.</summary>
    public string CreatePin(string userId, string username, string pin, TimeSpan? lifetime = null)
    {
        var token = Guid.NewGuid().ToString("N");
        _cache.Set(token, new Ticket(PinKind, username, pin, userId), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime ?? DefaultLifetime
        });
        return token;
    }

    public bool TryConsume(string token, out Ticket ticket)
    {
        lock (_consumeLock)
        {
            if (_cache.TryGetValue(token, out var value) && value is Ticket found)
            {
                ticket = found;
                _cache.Remove(token);
                return true;
            }

            ticket = default;
            return false;
        }
    }

    /// <summary>Carries a retry result across browser navigation without disclosing account state in the URL.</summary>
    public string CreateFailure(LoginFailure failure)
    {
        var token = Guid.NewGuid().ToString("N");
        _cache.Set("login-failure:" + token, failure, DefaultLifetime);
        return token;
    }

    /// <summary>Consumes a retry result once in the destination sign-in component.</summary>
    public bool TryConsumeFailure(string token, out LoginFailure? failure)
    {
        lock (_consumeLock)
        {
            var key = "login-failure:" + token;
            if (_cache.TryGetValue(key, out failure))
            {
                _cache.Remove(key);
                return true;
            }

            return false;
        }
    }

    /// <summary>Restores only non-secret input and the server's retry deadline.</summary>
    public sealed record LoginFailure(string Username, string? UserId, DateTimeOffset? RetryUntil, bool WasBlocked);

    public readonly record struct Ticket(string Kind, string Username, string? Password, string? UserId)
    {
        public bool IsPasswordLogin => string.Equals(Kind, PasswordKind, StringComparison.Ordinal);

        public bool IsPinLogin => string.Equals(Kind, PinKind, StringComparison.Ordinal);
    }
}
