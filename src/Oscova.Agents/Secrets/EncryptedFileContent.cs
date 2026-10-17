using Oscova.Agents.Utils;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Secrets;

/// <summary>Owns encrypted-file identification and authenticated content opening.</summary>
public static class EncryptedFileContent
{
    /// <summary>The suffix appended to encrypted file names.</summary>
    public const string FileExtension = ".enc";
    /// <summary>The protection purpose shared by encrypted-file readers and writers.</summary>
    public const string ProtectionPurpose = "encrypted-file";

    /// <summary>Returns whether the path uses the encrypted-file extension.</summary>
    public static bool IsEncryptedPath(string path) =>
        path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Attempts to decrypt an encrypted-file payload, leaving raw content unchanged on failure.</summary>
    public static bool TryDecrypt(
        string userId,
        string path,
        string content,
        IContentProtector protector,
        out string plaintext)
    {
        plaintext = content;
        if (!IsEncryptedPath(path))
        {
            return false;
        }

        var decrypted = protector.Unprotect(userId, ProtectionPurpose, content);
        if (decrypted is null)
        {
            return false;
        }

        plaintext = decrypted;
        return true;
    }

    /// <summary>Reads and decrypts a whole encrypted file, or returns null when it does not exist.</summary>
    public static async Task<string?> ReadAllTextAsync(
        string userId,
        string path,
        IContentProtector protector,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var ciphertext = await FileUtil.ReadAllTextSharedAsync(path, ct).ConfigureAwait(false);
        return protector.Unprotect(userId, ProtectionPurpose, ciphertext)
            ?? throw new InvalidDataException("Encrypted file data could not be opened. The vault may be locked.");
    }

    /// <summary>Encrypts and atomically replaces a whole file as one envelope.</summary>
    public static Task WriteAllTextAsync(
        string userId,
        string path,
        string plaintext,
        IContentProtector protector,
        CancellationToken ct = default) =>
        FileUtil.WriteAllTextAtomicAsync(
            path,
            protector.Protect(userId, ProtectionPurpose, plaintext),
            ct);
    /// <summary>Reads a complete encrypted file, or returns null when it is absent.</summary>
    public static string? ReadAllText(string userId, string path, IContentProtector protector)
    {
        if (!File.Exists(path)) { return null; }
        return protector.Unprotect(userId, ProtectionPurpose, FileUtil.ReadAllTextShared(path))
            ?? throw new InvalidDataException("Encrypted file data could not be opened. The vault may be locked.");
    }

    /// <summary>Encrypts and atomically replaces a whole file.</summary>
    public static void WriteAllText(string userId, string path, string plaintext, IContentProtector protector) =>
        FileUtil.WriteAllTextAtomic(path, protector.Protect(userId, ProtectionPurpose, plaintext));
}
