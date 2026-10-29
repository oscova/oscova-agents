using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Oscova.Agents.Infrastructure.Data;

namespace Oscova.Agents.Infrastructure.Auth;

/// <summary>Serializes account sign-ins and persists independent login and vault cooldowns.</summary>
public sealed class SignInThrottle(IDbContextFactory<ApplicationDbContext> contexts, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan IdleReset = TimeSpan.FromMinutes(15);
    // ponytail: one gate per encountered account until restart, add eviction only for sustained account churn.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly MemoryCache _unknownNames = new(new MemoryCacheOptions { SizeLimit = 4096 });
    private readonly Lock _unknownLock = new();

    /// <summary>Returns the gate held across an existing account's sign-in or credential write.</summary>
    public SemaphoreSlim GetAccountGate(string userId) => _gates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));

    /// <summary>Reads the authoritative deadline without extending a blocked attempt.</summary>
    public async Task<DateTimeOffset?> GetRetryUntilAsync(string userId, CredentialCategory category, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var state = await db.SignInAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.Category == category, ct).ConfigureAwait(false);
        return ActiveDeadline(state, clock.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>Records one wrong credential while the caller holds the account gate or vault transition lock.</summary>
    public async Task<DateTimeOffset?> RecordFailureAsync(string userId, CredentialCategory category, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var state = await db.SignInAttempts.SingleOrDefaultAsync(x => x.UserId == userId && x.Category == category, ct).ConfigureAwait(false);
        if (state is null)
        {
            state = new SignInAttemptState { UserId = userId, Category = category };
            db.SignInAttempts.Add(state);
        }

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var blocked = ActiveDeadline(state, now);
        if (blocked is not null) { return blocked; }

        RecordFailure(state, now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ActiveDeadline(state, now);
    }

    /// <summary>Clears the admitted category after its successful verification workflow.</summary>
    public async Task ResetAsync(string userId, CredentialCategory category, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var state = await db.SignInAttempts.FindAsync([userId, category], ct).ConfigureAwait(false);
        if (state is not null)
        {
            db.SignInAttempts.Remove(state);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Applies the same response schedule to unknown names without creating persistent accounts.</summary>
    public (DateTimeOffset? RetryUntil, bool WasBlocked) RejectUnknown(string normalizedName)
    {
        lock (_unknownLock)
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var state = _unknownNames.Get<SignInAttemptState>(normalizedName) ?? new SignInAttemptState();
            var blocked = ActiveDeadline(state, now);
            if (blocked is not null) { return (blocked, true); }

            RecordFailure(state, now);
            _unknownNames.Set(normalizedName, state, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = IdleReset
            });
            return (ActiveDeadline(state, now), false);
        }
    }

    private static DateTimeOffset? ActiveDeadline(SignInAttemptState? state, long now) =>
        state?.RetryUntilUnixMilliseconds is long until && until > now
            ? DateTimeOffset.FromUnixTimeMilliseconds(until)
            : null;

    private static void RecordFailure(SignInAttemptState state, long now)
    {
        if (state.LastFailureUnixMilliseconds is long last && now - last >= IdleReset.TotalMilliseconds)
        {
            state.FailureCount = 0;
        }

        state.FailureCount = Math.Min(state.FailureCount + 1, 7);
        var seconds = state.FailureCount switch { < 3 => 0, 3 => 5, 4 => 10, 5 => 20, 6 => 40, _ => 60 };
        state.LastFailureUnixMilliseconds = now;
        state.RetryUntilUnixMilliseconds = seconds == 0 ? null : now + seconds * 1000L;
    }

    /// <summary>Releases the bounded cache and account gates when the host stops.</summary>
    public void Dispose()
    {
        _unknownNames.Dispose();
        foreach (var gate in _gates.Values) { gate.Dispose(); }
    }
}
