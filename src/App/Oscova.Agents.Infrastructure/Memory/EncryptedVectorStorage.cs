using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Vector;

namespace Oscova.Agents.Infrastructure.Memory;

/// <summary>Composes encrypted vector indexes and coordinates their physical storage lifetime.</summary>
public static class EncryptedVectorStorage
{
    // ponytail: one gate serializes vector IO across users, split by user only if measured contention requires it.
    internal static SemaphoreSlim AccessGate { get; } = new(1, 1);

    /// <summary>Creates an index whose file and database access requires an unlocked vault.</summary>
    public static DurableVectorIndex CreateIndex(
        IVaultDataProtector vault,
        string userId,
        string databasePath,
        string manifestPath,
        string collectionName,
        ILogger logger) =>
        new(
            databasePath,
            manifestPath,
            collectionName,
            logger,
            vault,
            userId,
            () => OpenStore(vault, userId, databasePath),
            ct => AcquireAsync(vault, userId, ct));

    /// <summary>Rejects retained scopes after their vault key has been forgotten.</summary>
    public static void EnsureUnlocked(IVaultDataProtector vault, string userId)
    {
        if (!vault.IsAvailable(userId))
        {
            throw new ConfidentialDataLockedException("Unlock your Vault Password before accessing vector storage.");
        }
    }

    /// <summary>Creates a short-lived keyed store while the caller owns storage access.</summary>
    internal static VectorStoreService OpenStore(IVaultDataProtector vault, string userId, string databasePath)
    {
        byte[] key = vault.DeriveVectorDatabaseKey(userId);
        try
        {
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Password = "raw:" + Convert.ToHexString(key),
                Pooling = false
            }.ToString();
            return new VectorStoreService(databasePath, connectionString);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<IDisposable> AcquireAsync(
        IVaultDataProtector vault, string userId, CancellationToken ct)
    {
        await AccessGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUnlocked(vault, userId);
            return new AccessLease();
        }
        catch
        {
            AccessGate.Release();
            throw;
        }
    }

    private sealed class AccessLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                AccessGate.Release();
            }
        }
    }
}
