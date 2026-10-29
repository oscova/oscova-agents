using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Secrets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Host.Server.Services;

/// <summary>Keeps the authorized original destination separate from browser file identities.</summary>
public sealed record WorkspaceFileTarget(string Path, IReadOnlyList<AllowedDirectory> Directories,
    ProjectDefinition? NodeProject = null);

/// <summary>Opens and protects editable text while keeping node vault keys on the server.</summary>
public sealed class WorkspaceFileEditingService(NodeProjectExecutor nodes, IContentProtector protector, OperatorVaultAccess vault)
{
    /// <summary>Returns how the target is protected. Node projects never hold the server-owned shared folder.</summary>
    public async Task<FileProtection> GetProtectionAsync(string userId, WorkspaceFileTarget target, CancellationToken ct) =>
        target.NodeProject is null
            ? await vault.GetProtectionAsync(userId, target.Path, ct).ConfigureAwait(false)
            : new(userId, EncryptedFileContent.ProtectionPurpose);

    /// <summary>Loads complete editable text from its original machine and current user vault.</summary>
    public async Task<ProjectFilePreview> ReadAsync(string userId, WorkspaceFileTarget target, CancellationToken ct)
    {
        var stored = await ReadStoredAsync(userId, target, ct).ConfigureAwait(false);
        return Open(await GetProtectionAsync(userId, target, ct).ConfigureAwait(false), target.Path, stored);
    }

    /// <summary>Preserves trusted storage format and rechecks the stored revision at replacement.</summary>
    public async Task<ProjectFileSaveResult> SaveAsync(string userId, WorkspaceFileTarget target,
        SaveProjectFileRequest request, CancellationToken ct)
    {
        if (request.Text is null || request.ExpectedRevision is null || request.ExpectedRevision.Length != 64 ||
            !request.ExpectedRevision.All(Uri.IsHexDigit))
        { throw new ArgumentException("Text and a 64-character SHA-256 ExpectedRevision are required."); }

        var stored = await ReadStoredAsync(userId, target, ct).ConfigureAwait(false);
        var protection = await GetProtectionAsync(userId, target, ct).ConfigureAwait(false);
        var baseline = Open(protection, target.Path, stored);
        if (!baseline.CanEdit) { throw new UnauthorizedAccessException("The file is unavailable for editing under current access."); }

        if (!string.Equals(stored.Revision, request.ExpectedRevision, StringComparison.OrdinalIgnoreCase))
        { return new(stored.Revision!, Conflict: true); }

        var bytes = StoredFileEditing.Encode(request.Text, baseline);
        if (EncryptedFileContent.IsEncryptedPath(target.Path))
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            bytes = Encoding.UTF8.GetBytes(protector.Protect(protection.KeyOwner, protection.Purpose, text));
        }

        if (target.NodeProject is { } project)
        {
            // Only stored replacement bytes cross the node boundary, never the user's vault key.
            var result = await nodes.InvokeAsync(userId, project, ProjectCapability.SaveEditableFile,
                new { path = target.Path, bytes, expectedRevision = request.ExpectedRevision },
                [UserPermissionKeys.Projects], null, ct).ConfigureAwait(false);
            var saved = result.Deserialize<ProjectFileSaveResult>(AIJsonUtilities.DefaultOptions)
                ?? throw new InvalidDataException("The node returned no confirmed save response.");
            if (!saved.Conflict) { return saved; }

            var current = await ReadAsync(userId, target, ct).ConfigureAwait(false);
            if (!current.CanEdit) { throw new UnauthorizedAccessException("The changed file is unavailable for editing."); }

            return new(current.Revision!, Conflict: true);
        }

        return await StoredFileEditing.SaveAsync(target.Directories, target.Path, bytes,
            request.ExpectedRevision, ct).ConfigureAwait(false);
    }

    private async Task<StoredFileSnapshot> ReadStoredAsync(string userId, WorkspaceFileTarget target, CancellationToken ct)
    {
        if (target.NodeProject is not { } project)
        { return await StoredFileEditing.ReadAsync(target.Directories, target.Path, ct).ConfigureAwait(false); }

        var result = await nodes.InvokeAsync(userId, project, ProjectCapability.ReadEditableFile,
            new { path = target.Path }, [UserPermissionKeys.Projects], null, ct).ConfigureAwait(false);
        return result.Deserialize<StoredFileSnapshot>(AIJsonUtilities.DefaultOptions)
            ?? throw new InvalidDataException("The node returned no complete file snapshot.");
    }

    private ProjectFilePreview Open(FileProtection protection, string path, StoredFileSnapshot stored)
    {
        if (stored.State != ProjectReadState.Ready) { return new(stored.State); }

        var bytes = stored.Bytes;
        if (EncryptedFileContent.IsEncryptedPath(path))
        {
            if (!protector.IsAvailable(protection.KeyOwner)) { return new(ProjectReadState.Unavailable); }

            try
            {
                var ciphertext = new UTF8Encoding(false, true).GetString(bytes);
                if (!EncryptedFileContent.TryDecrypt(protection.KeyOwner, path, ciphertext, protector, out var plaintext, protection.Purpose))
                { return new(ProjectReadState.Unavailable); }

                bytes = new UTF8Encoding(false, true).GetBytes(plaintext);
            }
            catch (DecoderFallbackException) { return new(ProjectReadState.Unavailable); }
        }

        return StoredFileEditing.Decode(bytes, stored.Revision, stored.CanEdit);
    }
}
