using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Data;

namespace Oscova.Agents.Infrastructure.Auth;

/// <summary>Stores operator grants in Identity claims and reads them afresh for execution.</summary>
public sealed class UserPermissionService(IDbContextFactory<ApplicationDbContext> contexts, ExecutionPermissionRegistry executions)
    : IUserPermissionService, IDisposable
{
    public const string ClaimType = "oscova.permission";
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <inheritdoc />
    public async Task<string?> GetOperatorIdAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await (
            from assignment in db.UserRoles
            join role in db.Roles on assignment.RoleId equals role.Id
            where role.NormalizedName == "ADMIN"
            select assignment.UserId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsOperatorAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await IsOperatorAsync(db, userId, ct).ConfigureAwait(false);
    }

    private static Task<bool> IsOperatorAsync(ApplicationDbContext db, string userId, CancellationToken ct) => (
        from assignment in db.UserRoles
        join role in db.Roles on assignment.RoleId equals role.Id
        where assignment.UserId == userId && role.NormalizedName == "ADMIN"
        select assignment.UserId).AnyAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetGrantedAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.Users.AnyAsync(user => user.Id == userId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The account is unavailable.");
        }

        var isOperator = await IsOperatorAsync(db, userId, ct).ConfigureAwait(false);
        if (isOperator) { return UserPermissionKeys.All; }

        var grants = await db.UserClaims.AsNoTracking()
            .Where(claim => claim.UserId == userId && claim.ClaimType == ClaimType)
            .Select(claim => claim.ClaimValue)
            .ToArrayAsync(ct).ConfigureAwait(false);
        return UserPermissionKeys.All.Where(key => grants.Contains(key, StringComparer.Ordinal)).ToArray();
    }

    /// <inheritdoc />
    public async Task RequireAsync(string userId, string permission, CancellationToken ct = default)
    {
        var grants = await GetGrantedAsync(userId, ct).ConfigureAwait(false);
        if (!grants.Contains(permission, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException($"Permission '{permission}' is disabled. Ask the operator to enable it.");
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(string userId, IReadOnlyList<string> permissions, CancellationToken ct = default)
    {
        var requested = permissions.ToHashSet(StringComparer.Ordinal);
        if (requested.Except(UserPermissionKeys.All, StringComparer.Ordinal).Any())
        {
            throw new ArgumentException("Permissions contain an unsupported key.", nameof(permissions));
        }

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (!await db.Users.AnyAsync(user => user.Id == userId, ct).ConfigureAwait(false))
            {
                throw new KeyNotFoundException("The account is unavailable.");
            }

            var previous = await db.UserClaims
                .Where(claim => claim.UserId == userId && claim.ClaimType == ClaimType)
                .ToArrayAsync(ct).ConfigureAwait(false);
            db.UserClaims.RemoveRange(previous);
            db.UserClaims.AddRange(requested.Select(permission => new IdentityUserClaim<string>
            {
                UserId = userId,
                ClaimType = ClaimType,
                ClaimValue = permission
            }));
            // One SaveChanges transaction publishes the complete grant set without a partial revoke window.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            // Complete revocation even if the browser leaves after the permissions were committed.
            await executions.ApplyAsync(userId, requested.ToArray()).ConfigureAwait(false);
        }
        finally { _saveGate.Release(); }
    }

    /// <inheritdoc />
    public void Dispose() => _saveGate.Dispose();
}
