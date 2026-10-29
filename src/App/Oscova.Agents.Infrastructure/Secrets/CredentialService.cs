using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Infrastructure.Data;

namespace Oscova.Agents.Infrastructure.Secrets;

public sealed class CredentialService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IVaultDataProtector vaultDataProtector,
    string userId,
    Oscova.Agents.Core.Interfaces.IUserPermissionService permissions) : ICredentialService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<CredentialListItem>> GetAllAsync(CancellationToken ct = default)
    {
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var rows = await db.UserCredentials
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct).ConfigureAwait(false);

        var items = new List<CredentialListItem>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(new CredentialListItem
            {
                CredentialKey = row.CredentialKey,
                Alias = row.Alias,
                Description = row.Description,
                IsSet = HasAnyValue(row.Data),
                IsManaged = row.IsManaged,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt
            });
        }

        return items
            .OrderBy(x => x.Alias, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CredentialKey, StringComparer.Ordinal)
            .ToList();
        }
    }

    public async Task<IReadOnlyList<UserCredential>> GetAllRowsAsync(CancellationToken ct = default)
    {
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.UserCredentials
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<UserCredential?> GetByKeyAsync(string credentialKey, CancellationToken ct = default)
    {
        var normalizedCredentialKey = NormalizeCredentialKey(credentialKey);

        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.UserCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.UserId == userId && x.CredentialKey == normalizedCredentialKey,
                ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> HasValueAsync(string credentialKey, string fieldName, CancellationToken ct = default)
    {
        var credential = await GetByKeyAsync(credentialKey, ct).ConfigureAwait(false);
        if (credential is null)
        {
            return false;
        }

        var fields = ParseData(credential.Data);
        if (!fields.TryGetValue(fieldName, out var entry))
        {
            return false;
        }

        return entry.Source == CredentialFieldSource.Env
            ? !string.IsNullOrWhiteSpace(entry.Env)
            : !string.IsNullOrWhiteSpace(entry.Value);
    }

    public async Task<string?> ResolveFieldAsync(string credentialKey, string fieldName, CancellationToken ct = default)
    {
        var credential = await GetByKeyAsync(credentialKey, ct).ConfigureAwait(false);
        if (credential is null)
        {
            return null;
        }

        var fields = ParseData(credential.Data);
        return fields.TryGetValue(fieldName, out var entry)
            ? await ResolveEntryAsync(credential, credential.CredentialKey, fieldName, entry, ct).ConfigureAwait(false)
            : null;
    }

    public async Task<string?> ResolveFieldAsync(string credentialKey, CancellationToken ct = default)
    {
        var credential = await GetByKeyAsync(credentialKey, ct).ConfigureAwait(false);
        if (credential is null)
        {
            return null;
        }

        var fields = ParseData(credential.Data);
        if (fields.Count != 1)
        {
            return null;
        }

        var field = fields.First();
        return await ResolveEntryAsync(credential, credential.CredentialKey, field.Key, field.Value, ct).ConfigureAwait(false);
    }

    public Task SaveAsync(CredentialWrite credential, CancellationToken ct = default)
        => SaveAsync(credential, userId, ct);

    internal async Task SaveAsync(
        CredentialWrite credential,
        string protectionUserId,
        CancellationToken ct = default)
    {
        if (credential.Fields.Any(field => field.Source == CredentialFieldSource.Env))
        {
            await RequireServerEnvironmentAsync(ct).ConfigureAwait(false);
        }

        var resolvedUserId = GetRequiredUserId();
        if (string.IsNullOrWhiteSpace(protectionUserId))

        {
            throw new InvalidOperationException("A protection user is required to save stored secrets.");
        }

        var normalizedCredentialKey = NormalizeCredentialKey(credential.CredentialKey);
        var now = DateTimeOffset.UtcNow;

        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entity = await db.UserCredentials.SingleOrDefaultAsync(
            x => x.UserId == resolvedUserId && x.CredentialKey == normalizedCredentialKey,
            ct).ConfigureAwait(false);

        // Preserve-on-blank needs the field set already stored on the row, if any.
        var existingFields = entity is null
            ? new Dictionary<string, CredentialFieldEntry>(StringComparer.Ordinal)
            : ParseData(entity.Data);

        var suppliesNewSecret = credential.Fields.Any(field =>
            field.Source == CredentialFieldSource.Stored &&
            field.IsSecret &&
            !string.IsNullOrWhiteSpace(field.Value));
        var existingHasStoredSecret = HasStoredSecret(existingFields);
        if (suppliesNewSecret &&
            existingHasStoredSecret &&
            !string.Equals(entity?.ProtectionUserId, protectionUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This credential is protected by a different user.");
        }

        var targetProtectionUserId = suppliesNewSecret ? protectionUserId : entity?.ProtectionUserId;
        var fields = new Dictionary<string, CredentialFieldEntry>(StringComparer.Ordinal);
        foreach (var field in credential.Fields)
        {
            existingFields.TryGetValue(field.Name, out var existingEntry);
            fields[field.Name] = BuildEntry(
                normalizedCredentialKey,
                field,
                existingEntry,
                targetProtectionUserId ?? protectionUserId);
        }

        var data = SerializeData(fields);
        var resultingProtectionUserId = HasStoredSecret(fields) ? targetProtectionUserId : null;

        if (entity is null)
        {
            db.UserCredentials.Add(new UserCredential
            {
                Id = Guid.NewGuid(),
                UserId = resolvedUserId,
                ProtectionUserId = resultingProtectionUserId,
                CredentialKey = normalizedCredentialKey,
                Alias = credential.Alias.Trim(),
                Description = credential.Description.NormalizeOptional(),
                Data = data,
                IsManaged = credential.IsManaged,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            entity.Alias = credential.Alias.Trim();
            entity.Description = credential.Description.NormalizeOptional();
            entity.Data = data;
            entity.ProtectionUserId = resultingProtectionUserId;
            entity.IsManaged = credential.IsManaged;
            entity.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(string credentialKey, CancellationToken ct = default)
    {
        var resolvedUserId = GetRequiredUserId();
        var normalizedCredentialKey = NormalizeCredentialKey(credentialKey);

        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entity = await db.UserCredentials.SingleOrDefaultAsync(
            x => x.UserId == resolvedUserId && x.CredentialKey == normalizedCredentialKey,
            ct).ConfigureAwait(false);

        if (entity is null)
        {
            return;
        }

        db.UserCredentials.Remove(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<int> DeleteAllForUserAsync(string userId, CancellationToken ct = default)
    {
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entities = await db.UserCredentials
            .Where(x => x.UserId == userId)
            .ToListAsync(ct).ConfigureAwait(false);

        if (entities.Count > 0)
        {
            db.UserCredentials.RemoveRange(entities);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return entities.Count;
        }
    }

    // Builds one stored field entry, applying preserve-on-blank for secret fields.
    private CredentialFieldEntry BuildEntry(
        string credentialKey,
        CredentialFieldWrite field,
        CredentialFieldEntry? existing,
        string protectionUserId)
    {
        if (field.Source == CredentialFieldSource.Env)
        {
            return new CredentialFieldEntry
            {
                Source = CredentialFieldSource.Env,
                Secret = field.IsSecret,
                Env = field.EnvVariable?.Trim()
            };
        }

        var raw = field.Value?.Trim() ?? string.Empty;
        string? storedValue;
        if (field.IsSecret && raw.Length == 0)
        {
            // A blank secret value means "keep the current value".
            storedValue = existing is not null && existing.Source != CredentialFieldSource.Env ? existing.Value : null;
        }
        else
        {
            var purpose = $"credential\0{credentialKey}\0{field.Name}";
            storedValue = field.IsSecret
                ? vaultDataProtector.Protect(protectionUserId, purpose, raw)
                : raw;
        }

        return new CredentialFieldEntry
        {
            Source = CredentialFieldSource.Stored,
            Secret = field.IsSecret,
            Value = storedValue
        };
    }

    // Resolution algorithm: env var lookup, decrypt, or plain value.
    private async Task<string?> ResolveEntryAsync(
        UserCredential credential,
        string normalizedCredentialKey,
        string fieldName,
        CredentialFieldEntry entry, CancellationToken ct)
    {
        if (entry.Source == CredentialFieldSource.Env)
        {
            await RequireServerEnvironmentAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry.Env)
                ? null
                : Environment.GetEnvironmentVariable(entry.Env);
        }

        if (string.IsNullOrEmpty(entry.Value))
        {
            return entry.Value;
        }

        if (!entry.Secret)
        {
            return entry.Value;
        }

        if (credential.ProtectionUserId is null)
        {
            return null;
        }

        var purpose = $"credential\0{normalizedCredentialKey}\0{fieldName}";
        return vaultDataProtector.Unprotect(credential.ProtectionUserId, purpose, entry.Value);
    }

    /// <summary>Restricts server environment access to the operator and operator-managed system credentials.</summary>
    private async Task RequireServerEnvironmentAsync(CancellationToken ct)
    {
        if (userId != Core.WellKnownUsers.SystemId && !await permissions.IsOperatorAsync(userId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Only the operator can reference server environment variables. Store your personal credential value instead.");
        }
    }

    private static bool HasStoredSecret(IReadOnlyDictionary<string, CredentialFieldEntry> fields)
        => fields.Values.Any(entry =>
            entry.Source == CredentialFieldSource.Stored &&
            entry.Secret &&
            !string.IsNullOrWhiteSpace(entry.Value));

    private static bool HasAnyValue(string data)
    {
        Dictionary<string, CredentialFieldEntry> fields;
        try
        {
            fields = ParseData(data);
        }
        catch (InvalidOperationException exception) when (exception.InnerException is JsonException)
        {
            return false;
        }

        foreach (var entry in fields.Values)
        {
            if (entry.Source == CredentialFieldSource.Env)
            {
                if (!string.IsNullOrWhiteSpace(entry.Env))
                {
                    return true;
                }
            }
            else if (!string.IsNullOrWhiteSpace(entry.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, CredentialFieldEntry> ParseData(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return new Dictionary<string, CredentialFieldEntry>(StringComparer.Ordinal);
        }

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, CredentialFieldEntry>>(data, JsonOptions);
            return map is null
                ? new Dictionary<string, CredentialFieldEntry>(StringComparer.Ordinal)
                : new Dictionary<string, CredentialFieldEntry>(map, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Stored credential field data is malformed.", ex);
        }
    }

    private static string SerializeData(Dictionary<string, CredentialFieldEntry> map)
        => JsonSerializer.Serialize(map, JsonOptions);

    private string GetRequiredUserId()
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("User must be authenticated to manage credentials.");
        }

        return userId;
    }

    private static string NormalizeCredentialKey(string credentialKey)
    {
        if (string.IsNullOrWhiteSpace(credentialKey))
        {
            throw new ArgumentException("Credential key is required.", nameof(credentialKey));
        }

        return credentialKey.Trim().ToLowerInvariant();
    }
}
