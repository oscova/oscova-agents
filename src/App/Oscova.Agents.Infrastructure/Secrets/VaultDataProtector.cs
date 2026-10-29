using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Memory;

namespace Oscova.Agents.Infrastructure.Secrets;

/// <summary>
/// Keeps each user's data-encryption key in memory and persists only its password-wrapped envelope.
/// </summary>
public sealed class VaultDataProtector(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    SignInThrottle throttle,
    ILogger<VaultDataProtector> logger) : IVaultDataProtector
{
    private const string EnvelopePurpose = "Oscova.ConfidentialKeyEnvelope.v1";
    private const string DataPurpose = "Oscova.ConfidentialData.v1";

    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _transitionLock = new(1, 1);

    /// <inheritdoc />
    public bool IsAvailable(string userId) => _keys.ContainsKey(userId);

    /// <inheritdoc />
    public async Task<VaultKeyState> GetStateAsync(string userId, CancellationToken ct = default)
    {
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var envelope = await db.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.VaultKeyEnvelope)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);

            var isConfigured = envelope is not null;
            return new VaultKeyState(isConfigured, isConfigured && _keys.ContainsKey(userId));
        }
    }

    /// <inheritdoc />
    public async Task SetPasswordAsync(string userId, string password, CancellationToken ct = default)
    {
        // Publish initial user keys only between backup snapshots.
        await EncryptedVectorStorage.AccessGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                await using (db.ConfigureAwait(false))
                {
                    var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The user no longer exists.");
                    if (user.VaultKeyEnvelope is not null)
                    {
                        throw new InvalidOperationException("A Vault Password is already configured.");
                    }

                    var key = RandomNumberGenerator.GetBytes(PassphraseEnvelope.KeyLength);
                    try
                    {
                        user.VaultKeyEnvelope = PassphraseEnvelope.Create(
                            key,
                            password,
                            GetEnvelopeAssociatedData(userId));
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                        StoreKey(userId, key);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }
                }
            }
            finally
            {
                _transitionLock.Release();
            }
        }
        finally { EncryptedVectorStorage.AccessGate.Release(); }
    }

    /// <inheritdoc />
    public async Task<VaultPasswordResult> UnlockAsync(string userId, string password, CancellationToken ct = default)
    {
        await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Keep admission, verification and retry persistence under the same transition lock.
            var retryUntil = await throttle.GetRetryUntilAsync(userId, CredentialCategory.Vault, ct).ConfigureAwait(false);
            if (retryUntil is not null) { return new(false, retryUntil); }

            var envelope = await LoadEnvelopeAsync(userId, ct).ConfigureAwait(false);
            if (envelope is null)
            {
                throw new InvalidOperationException("A Vault Password is not configured.");
            }

            var key = PassphraseEnvelope.TryOpen(
                envelope,
                password,
                GetEnvelopeAssociatedData(userId),
                PassphraseEnvelope.KeyLength);
            if (key is null)
            {
                return new(false, await throttle.RecordFailureAsync(userId, CredentialCategory.Vault, ct).ConfigureAwait(false));
            }

            try
            {
                // A failed reset must not publish a newly decrypted key.
                await throttle.ResetAsync(userId, CredentialCategory.Vault, ct).ConfigureAwait(false);
                StoreKey(userId, key);
                return new(true, null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<VaultPasswordResult> ChangePasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default)
    {
        await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var retryUntil = await throttle.GetRetryUntilAsync(userId, CredentialCategory.Vault, ct).ConfigureAwait(false);
            if (retryUntil is not null) { return new(false, retryUntil); }

            var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using (db.ConfigureAwait(false))
            {
                var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The user no longer exists.");
                if (user.VaultKeyEnvelope is null)
                {
                    throw new InvalidOperationException("A Vault Password is not configured.");
                }

                var key = PassphraseEnvelope.TryOpen(
                    user.VaultKeyEnvelope,
                    currentPassword,
                    GetEnvelopeAssociatedData(userId),
                    PassphraseEnvelope.KeyLength);
                if (key is null)
                {
                    return new(false, await throttle.RecordFailureAsync(userId, CredentialCategory.Vault, ct).ConfigureAwait(false));
                }

                try
                {
                    // Persist accepted verification before changing the envelope or publishing its key.
                    await throttle.ResetAsync(userId, CredentialCategory.Vault, ct).ConfigureAwait(false);
                    user.VaultKeyEnvelope = PassphraseEnvelope.Create(
                        key,
                        newPassword,
                        GetEnvelopeAssociatedData(userId));
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    StoreKey(userId, key);
                    return new(true, null);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    /// <inheritdoc />
    public string Protect(string userId, string purpose, string plaintext)
    {
        if (!_keys.TryGetValue(userId, out var key))
        {
            throw new ConfidentialDataLockedException(
                "Set or unlock your Vault Password before saving stored secrets.");
        }

        var nonce = RandomNumberGenerator.GetBytes(PassphraseEnvelope.NonceLength);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[PassphraseEnvelope.TagLength];
        try
        {
            using var aes = new AesGcm(key, PassphraseEnvelope.TagLength);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, GetDataAssociatedData(userId, purpose));
            return $"v1.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(ciphertext)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    /// <inheritdoc />
    public string? Unprotect(string userId, string purpose, string ciphertext)
    {
        if (!_keys.TryGetValue(userId, out var key))
        {
            return null;
        }

        try
        {
            var segments = ciphertext.Split('.');
            if (segments.Length != 4 || !string.Equals(segments[0], "v1", StringComparison.Ordinal))
            {
                throw new FormatException("Unsupported confidential-data payload format.");
            }

            var nonce = Convert.FromBase64String(segments[1]);
            var tag = Convert.FromBase64String(segments[2]);
            var encrypted = Convert.FromBase64String(segments[3]);
            if (nonce.Length != PassphraseEnvelope.NonceLength || tag.Length != PassphraseEnvelope.TagLength)
            {
                throw new FormatException("Invalid confidential-data payload length.");
            }

            var plaintext = new byte[encrypted.Length];
            try
            {
                using var aes = new AesGcm(key, PassphraseEnvelope.TagLength);
                aes.Decrypt(nonce, encrypted, tag, plaintext, GetDataAssociatedData(userId, purpose));
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            logger.LogWarning(
                "Confidential data for user {UserId} could not be unprotected ({FailureCategory}).",
                userId,
                ex is CryptographicException ? "authentication" : "format");
            return null;
        }
    }

    /// <summary>Derives the portable database key without exposing the vault root key.</summary>
    public byte[] DeriveVectorDatabaseKey(string userId)
    {
        _transitionLock.Wait();
        try
        {
            if (!_keys.TryGetValue(userId, out byte[]? key))
            {
                throw new ConfidentialDataLockedException("Unlock your Vault Password before accessing vector storage.");
            }

            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                key,
                32,
                info: Encoding.UTF8.GetBytes($"Oscova.VectorDatabase.v1\0{userId}"));
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    /// <inheritdoc />
    public void Forget(string userId)
    {
        EncryptedVectorStorage.AccessGate.Wait();
        try
        {
            _transitionLock.Wait();
            try
            {
                if (_keys.TryRemove(userId, out var key))
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            finally
            {
                _transitionLock.Release();
            }
        }
        finally
        {
            EncryptedVectorStorage.AccessGate.Release();
        }
    }

    private async Task<string?> LoadEnvelopeAsync(string userId, CancellationToken ct)
    {
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var result = await db.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => new { user.VaultKeyEnvelope })
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("The user no longer exists.");
            return result.VaultKeyEnvelope;
        }
    }

    private void StoreKey(string userId, byte[] key)
    {
        var storedKey = key.ToArray();
        if (!_keys.TryAdd(userId, storedKey))
        {
            // Unlock and password change recover the same DEK, so keep the copy already in use.
            CryptographicOperations.ZeroMemory(storedKey);
        }
    }

    private static byte[] GetEnvelopeAssociatedData(string userId)
        => Encoding.UTF8.GetBytes($"{EnvelopePurpose}\0{userId}");

    private static byte[] GetDataAssociatedData(string userId, string purpose)
        => Encoding.UTF8.GetBytes($"{DataPurpose}\0{userId}\0{purpose}");

}
