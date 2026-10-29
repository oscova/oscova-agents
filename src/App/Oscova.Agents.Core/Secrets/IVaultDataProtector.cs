using Oscova.Agents.Secrets;

namespace Oscova.Agents.Core.Secrets;

/// <summary>
/// Protects durable user data with a per-user key that is unlocked only in process memory.
/// </summary>
public interface IVaultDataProtector : IContentProtector
{
    /// <summary>Gets whether the user has configured and unlocked their vault.</summary>
    Task<VaultKeyState> GetStateAsync(string userId, CancellationToken ct = default);

    /// <summary>Creates and unlocks the user's vault key.</summary>
    Task SetPasswordAsync(string userId, string password, CancellationToken ct = default);

    /// <summary>Attempts to unlock the user's vault key.</summary>
    Task<VaultPasswordResult> UnlockAsync(string userId, string password, CancellationToken ct = default);

    /// <summary>Rewraps the user's existing vault key with a new password.</summary>
    Task<VaultPasswordResult> ChangePasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken ct = default);

    /// <summary>Returns a purpose-separated vector database key that the caller must clear after use.</summary>
    byte[] DeriveVectorDatabaseKey(string userId);

    /// <summary>Forgets and clears the user's in-memory key.</summary>
    void Forget(string userId);
}

/// <summary>Describes the persisted and in-memory state of a user's vault key.</summary>
public readonly record struct VaultKeyState(bool IsConfigured, bool IsUnlocked);

/// <summary>Distinguishes accepted passwords, wrong passwords and admission cooldowns.</summary>
public readonly record struct VaultPasswordResult(bool IsAccepted, DateTimeOffset? RetryUntil);
