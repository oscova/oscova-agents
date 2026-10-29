using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Infrastructure.Auth;

/// <summary>Issues independently revocable node credentials without granting ordinary account API access.</summary>
public sealed class NodeCredentialStore(IDbContextFactory<ApplicationDbContext> contexts, IContentProtector protector)
{
    public const string Provider = "Oscova.Node";
    public const string NodeIdClaim = "oscova.node_id";
    private const string NamePurpose = "node-display-name";

    /// <summary>Registers a named device and returns its secret only to the enrolling account.</summary>
    public async Task<NodeEnrollment> EnrollAsync(string userId, string name, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100)
        {
            throw new ArgumentException("Node name must contain 1 to 100 characters.", nameof(name));
        }

        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.Users.AnyAsync(user => user.Id == userId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("The account is unavailable.");
        }

        var id = Guid.NewGuid().ToString("N");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        db.UserTokens.Add(new IdentityUserToken<string>
        {
            UserId = userId,
            LoginProvider = Provider,
            Name = id,
            Value = JsonSerializer.Serialize(new Credential(protector.Protect(userId, NamePurpose, name), hash))
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new(new(id, name), $"{id}.{secret}");
    }

    /// <summary>Authenticates a node secret against its current stored hash.</summary>
    public async Task<NodeIdentity?> AuthenticateAsync(string token, CancellationToken ct)
    {
        if (token.Length != 97 || token[32] != '.' || !Guid.TryParseExact(token[..32], "N", out _))
        {
            return null;
        }

        var id = token[..32];
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.UserTokens.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.LoginProvider == Provider && entry.Name == id, ct)
            .ConfigureAwait(false);
        if (row is null) { return null; }

        var credential = Read(row);
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token[33..]));
        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, Convert.FromHexString(credential.Hash)))
        {
            return null;
        }

        return new(row.UserId, row.Name);
    }

    /// <summary>Reads the devices owned by the authenticated account.</summary>
    public async Task<IReadOnlyList<NodeDefinition>> ListAsync(string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.UserTokens.AsNoTracking()
            .Where(entry => entry.UserId == userId && entry.LoginProvider == Provider)
            .ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(row => new NodeDefinition(row.Name,
                protector.Unprotect(userId, NamePurpose, Read(row).Name)
                ?? throw new InvalidDataException($"Node name '{row.Name}' could not be decrypted.")))
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Rechecks enrollment for work admitted on an already authenticated connection.</summary>
    public async Task<bool> IsEnrolledAsync(string userId, string nodeId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.UserTokens.AnyAsync(entry => entry.UserId == userId &&
            entry.LoginProvider == Provider && entry.Name == nodeId, ct).ConfigureAwait(false);
    }

    /// <summary>Revokes only the selected account-owned node credential.</summary>
    public async Task<bool> RevokeAsync(string userId, string nodeId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.UserTokens.Where(entry => entry.UserId == userId &&
            entry.LoginProvider == Provider && entry.Name == nodeId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false) != 0;
    }

    private static Credential Read(IdentityUserToken<string> row) =>
        JsonSerializer.Deserialize<Credential>(row.Value ?? string.Empty)
        ?? throw new InvalidDataException($"Node registration '{row.Name}' is invalid.");

    private sealed record Credential(string Name, string Hash);
}
