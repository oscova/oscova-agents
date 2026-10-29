using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Plugins;

namespace Oscova.Agents.Infrastructure.Secrets;

public sealed class SystemCredentialService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IVaultDataProtector vaultDataProtector,
    Oscova.Agents.Core.Interfaces.IUserPermissionService permissions) : ISystemCredentialService
{
    private readonly CredentialService _inner = new(dbFactory, vaultDataProtector, WellKnownUsers.SystemId, permissions);

    public Task<IReadOnlyList<CredentialListItem>> GetAllAsync(CancellationToken ct = default) => _inner.GetAllAsync(ct);

    public Task<IReadOnlyList<UserCredential>> GetAllRowsAsync(CancellationToken ct = default) => _inner.GetAllRowsAsync(ct);

    public Task<UserCredential?> GetByKeyAsync(string credentialKey, CancellationToken ct = default) => _inner.GetByKeyAsync(credentialKey, ct);

    public Task<bool> HasValueAsync(string credentialKey, string fieldName, CancellationToken ct = default) => _inner.HasValueAsync(credentialKey, fieldName, ct);

    public Task<string?> ResolveFieldAsync(string credentialKey, string fieldName, CancellationToken ct = default) => _inner.ResolveFieldAsync(credentialKey, fieldName, ct);

    public Task<string?> ResolveFieldAsync(string credentialKey, CancellationToken ct = default) => _inner.ResolveFieldAsync(credentialKey, ct);

    public async Task SaveAsync(
        string protectionUserId,
        CredentialWrite credential,
        CancellationToken ct = default)
    {
        await EnsureSystemUserExistsAsync(ct).ConfigureAwait(false);
        await _inner.SaveAsync(credential, protectionUserId, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string credentialKey, CancellationToken ct = default)
    {
        await EnsureSystemUserExistsAsync(ct).ConfigureAwait(false);
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var key = credentialKey.Trim().ToLowerInvariant();
            var claim = await db.UserCredentials.SingleOrDefaultAsync(row =>
                row.UserId == WellKnownUsers.SystemId && row.CredentialKey == key &&
                row.OAuthProviderId != null, ct).ConfigureAwait(false);
            if (claim is not null)
            {
                claim.Data = "{}";
                claim.ProtectionUserId = null;
                claim.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
        }

        await _inner.DeleteAsync(credentialKey, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryClaimOAuthProviderAsync(string providerId, string pluginId, string credentialKey,
        CancellationToken ct = default)
    {
        await EnsureSystemUserExistsAsync(ct).ConfigureAwait(false);
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var existing = await db.UserCredentials.SingleOrDefaultAsync(row => row.OAuthProviderId == providerId, ct)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return string.Equals(existing.OwnerPluginId, pluginId, StringComparison.OrdinalIgnoreCase);
            }

            db.UserCredentials.Add(new UserCredential
            {
                UserId = WellKnownUsers.SystemId,
                CredentialKey = credentialKey,
                Alias = providerId,
                IsManaged = true,
                OAuthProviderId = providerId,
                OwnerPluginId = pluginId.ToLowerInvariant()
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    public async Task DeletePluginNamespaceAsync(string pluginId, CancellationToken ct = default)
    {
        var prefix = PluginCredentialKeys.Prefix(pluginId);
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.UserCredentials.Where(row => row.CredentialKey.StartsWith(prefix))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureSystemUserExistsAsync(CancellationToken ct)
    {
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var exists = await db.Users.AnyAsync(x => x.Id == WellKnownUsers.SystemId, ct).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        db.Users.Add(new ApplicationUser
        {
            Id = WellKnownUsers.SystemId,
            FullName = "System",
            UserName = WellKnownUsers.SystemId,
            NormalizedUserName = WellKnownUsers.SystemId.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
