using System.Security.Cryptography;
using System.Text;
using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Secrets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Core.Projects;

/// <summary>Transfers one complete bounded file without rereading independent chunks.</summary>
public sealed record StoredFileSnapshot(ProjectReadState State, byte[] Bytes, string? Revision, bool CanEdit);

/// <summary>Shares bounded snapshots and contained conditional replacement between server and node.</summary>
public static class StoredFileEditing
{
    // ponytail: one process save gate, use per-file gates if human saves contend.
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Reads and fingerprints exactly the same stored bytes under current file grants.</summary>
    public static async Task<StoredFileSnapshot> ReadAsync(IReadOnlyList<AllowedDirectory> directories,
        string path, CancellationToken ct)
    {
        var files = new WorkspaceFileAccess(() => directories);
        using var stream = files.OpenRead(path);
        var limit = EncryptedFileContent.IsEncryptedPath(path)
            ? UserFileLimits.MaxEncryptedEditSizeBytes : UserFileLimits.MaxEditSizeBytes;
        if (stream.Length > limit) { return new(ProjectReadState.TooLarge, [], null, false); }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (count == 0) { break; }

            if (buffer.Length + count > limit) { return new(ProjectReadState.TooLarge, [], null, false); }

            buffer.Write(chunk, 0, count);
        }

        var bytes = buffer.ToArray();
        var writable = true;
        try { PathHelper.ResolveAuthorizedHostPath(path, directories, WorkspacePathAccess.Write); }
        catch (UnauthorizedAccessException) { writable = false; }

        return new(ProjectReadState.Ready, bytes, Revision(bytes), writable);
    }

    /// <summary>Decodes complete supported UTF-8 text and retains its round-trip format.</summary>
    public static ProjectFilePreview Decode(byte[] bytes, string? revision = null, bool canEdit = false)
    {
        if (bytes.LongLength > UserFileLimits.MaxEditSizeBytes) { return new(ProjectReadState.TooLarge); }

        if (bytes.AsSpan().Contains((byte)0)) { return new(ProjectReadState.Binary); }

        try
        {
            var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
            var text = Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            if (text.Count(c => c == '\n') + 1 > ProjectAccessService.MaxTextLines)
            { return new(ProjectReadState.TooLarge); }

            return new(ProjectReadState.Ready, text, revision, canEdit, bom,
                text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        }
        catch (DecoderFallbackException) { return new(ProjectReadState.Binary); }
    }

    /// <summary>Encodes a draft with the trusted loaded format and checks the resulting byte count.</summary>
    public static byte[] Encode(string text, ProjectFilePreview baseline)
    {
        if (text?.Contains('\0') != false || text.Count(c => c == '\n') + 1 > ProjectAccessService.MaxTextLines)
        { throw new ArgumentException("Text must be UTF-8 without NUL characters and contain at most 5,000 lines."); }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (baseline.NewLine == "\r\n") { normalized = normalized.Replace("\n", "\r\n", StringComparison.Ordinal); }

        var bytes = new UTF8Encoding(baseline.HasUtf8Bom, true).GetBytes(normalized);
        if (baseline.HasUtf8Bom) { bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray(); }

        if (bytes.LongLength > UserFileLimits.MaxEditSizeBytes)
        { throw new ArgumentException("Text must contain at most 524,288 UTF-8 bytes."); }

        return bytes;
    }

    /// <summary>Rechecks stored bytes and current write grants before atomic contained replacement.</summary>
    public static async Task<ProjectFileSaveResult> SaveAsync(IReadOnlyList<AllowedDirectory> directories,
        string path, byte[] bytes, string expectedRevision, CancellationToken ct)
    {
        if (expectedRevision is null || expectedRevision.Length != 64 || !expectedRevision.All(Uri.IsHexDigit))
        { throw new ArgumentException("ExpectedRevision must contain a 64-character SHA-256 digest."); }

        var encrypted = EncryptedFileContent.IsEncryptedPath(path);
        if (encrypted)
        {
            // The server protector owns cipher selection, nodes check only the stored envelope marker.
            if (bytes.LongLength > UserFileLimits.MaxEncryptedEditSizeBytes ||
                !EncryptedFileContent.HasEnvelopeMarker(bytes))
            { throw new ArgumentException("Encrypted replacement must be a bounded encrypted-file envelope."); }
        }
        else if (Decode(bytes).State != ProjectReadState.Ready)
        { throw new ArgumentException("Replacement must be supported UTF-8 text within the byte and line limits."); }

        await SaveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(directories, path, ct).ConfigureAwait(false);
            if (!current.CanEdit) { throw new UnauthorizedAccessException("The file is not writable under current access."); }

            if (!encrypted && Decode(current.Bytes).State != ProjectReadState.Ready)
            { throw new InvalidDataException("The current file is no longer supported text."); }

            if (!string.Equals(current.Revision, expectedRevision, StringComparison.OrdinalIgnoreCase))
            { return new(current.Revision!, Conflict: true); }

            ct.ThrowIfCancellationRequested();
            // Physical replacement is atomic, comparison does not lock independent writers.
            new WorkspaceFileAccess(() => directories).Write(path, stream => stream.Write(bytes));
            return new(Revision(bytes));
        }
        finally { SaveGate.Release(); }
    }

    /// <summary>Fingerprints stored bytes rather than timestamps or decrypted presentation.</summary>
    public static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
