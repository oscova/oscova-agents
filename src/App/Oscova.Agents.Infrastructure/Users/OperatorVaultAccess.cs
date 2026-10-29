using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Secrets;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Users;

/// <summary>Gates shared configuration use on the operator's existing in-memory vault key.</summary>
public sealed class OperatorVaultAccess(
    IUserPermissionService permissions,
    IContentProtector protector,
    PendingDuressSelections pending)
{
    /// <summary>Lets the operator reach vault setup and requires operator readiness for other accounts.</summary>
    public async Task<bool> IsReadyAsync(string userId, CancellationToken ct = default)
    {
        var operatorId = await permissions.GetOperatorIdAsync(ct).ConfigureAwait(false);
        return operatorId is not null && (operatorId == userId ||
            (protector.IsAvailable(operatorId) && !pending.Contains(operatorId)));
    }

    /// <summary>Rejects regular-user operation while the shared configuration cannot be decrypted.</summary>
    public async Task<string> RequireAsync(string userId, CancellationToken ct = default)
    {
        var operatorId = await permissions.GetOperatorIdAsync(ct).ConfigureAwait(false);
        if (operatorId is null || (operatorId != userId &&
            (!protector.IsAvailable(operatorId) || pending.Contains(operatorId))))
        {
            throw new InvalidOperationException("The operator must unlock their vault before you can continue.");
        }

        return operatorId;
    }

    /// <summary>The protection purpose of shared folder files, kept apart so operator ciphertext copied in never opens.</summary>
    public const string SharedProtectionPurpose = "shared-encrypted-file";

    /// <summary>
    /// Returns how an encrypted file at a host path is protected. Files in the shared folder use the operator's key
    /// so every permitted member opens them, and every other file uses the user's own key.
    /// </summary>
    public static FileProtection GetProtection(string userId, string operatorId, string hostPath) =>
        IsSharedPath(hostPath) ? new(operatorId, SharedProtectionPurpose) : new(userId, EncryptedFileContent.ProtectionPurpose);

    /// <inheritdoc cref="GetProtection(string, string, string)"/>
    public async Task<FileProtection> GetProtectionAsync(string userId, string hostPath, CancellationToken ct = default) =>
        IsSharedPath(hostPath)
            ? new(await RequireAsync(userId, ct).ConfigureAwait(false), SharedProtectionPurpose)
            : new(userId, EncryptedFileContent.ProtectionPurpose);

    private static bool IsSharedPath(string hostPath) =>
        PathHelper.IsPathWithinDirectory(Path.GetFullPath(hostPath), PathUtil.SharedPath);
}

/// <summary>Names the vault key and protection purpose of one encrypted file.</summary>
public sealed record FileProtection(string KeyOwner, string Purpose);
