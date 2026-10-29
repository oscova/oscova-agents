using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Policies;
using Oscova.Agents.Secrets;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Authors encrypted interactive documents belonging to the current browser conversation.</summary>
[DisplayName("Visualization")]
public sealed class VisualizationToolset(UserServiceManager users, RunContextAccessor runs,
    ChatVisualizationService visualizations, IContentProtector protector, IChatAttachmentService attachments,
    IWorkspaceFileAccess? fileAccess = null) : IAgentToolset
{
    public static readonly string QualifiedToolName = ToolNaming.BuildToolsetQualifiedName<VisualizationToolset>(nameof(ShowAsync));

    private const string Preconditions = "\nRequires the directly selected agent in browser chat and an unlocked vault.\nUnavailable in user, agent or run local-only mode.\nRequires current-thread workspace access.";
    private const string IdDescription = "ID returned by creation in this thread. 1-64 ASCII letters, digits, underscores or hyphens. No path or extension.";

    /// <summary>Creates ciphertext through the shared encrypted file implementation.</summary>
    [DisplayName("create")]
    [AgentTool]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    [Description("Create an encrypted visualization and return its ID.\nHTML must contain non-whitespace content and 1-1048576 UTF-8 bytes.\nEmbed CSS, JavaScript and data. No remote resources or network calls.\nTo display it, call show with the returned ID.\nDo not write the ID in your reply." + Preconditions)]
    public async Task<string> CreateAsync(
        [Description("Complete self-contained HTML source, 1-1048576 UTF-8 bytes.")] string html,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(html) || Encoding.UTF8.GetByteCount(html) > ChatVisualizationService.MaxHtmlBytes)
        {
            throw new ToolFailureException("html must contain non-whitespace content and 1-1048576 UTF-8 bytes.");
        }

        var id = Guid.NewGuid().ToString("N");
        var context = await ResolveAsync(id, WorkspacePathAccess.Write, cancellationToken).ConfigureAwait(false);
        context.Files.WriteFile(context.Path, html);
        return JsonSerializer.Serialize(new { id });
    }

    /// <summary>Validates saved ciphertext and returns its durable display reference.</summary>
    [DisplayName("show")]
    [AgentTool]
    [Description("Show an existing visualization at this reply position.\nThe ID must exist in this thread and decrypt successfully.\nHTML must contain non-whitespace content and 1-1048576 UTF-8 bytes.\nEncrypted file must not exceed 2097152 bytes.\nAll appearances of an ID display its current content." + Preconditions)]
    public async Task<string> ShowAsync(
        [Description(IdDescription)] string id,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(id, WorkspacePathAccess.Read, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(context.Path))
        {
            // A mistaken plaintext write needs cleanup guidance, never a publication receipt.
            var grants = runs.Current!.AllowedDirectories;
            var plaintextPath = PathHelper.ResolveAuthorizedHostPath(Path.ChangeExtension(context.Path, null), grants,
                WorkspacePathAccess.Read, out _, out _);
            if (File.Exists(plaintextPath))
            {
                await visualizations.ReadFileAsync(context.Scope.UserId, context.Thread, plaintextPath,
                    PathHelper.ResolveToAgentPath(plaintextPath, grants), cancellationToken).ConfigureAwait(false);
            }
        }

        await visualizations.ReadFileAsync(context.Scope.UserId, context.Thread, context.Path, context.AgentPath, cancellationToken).ConfigureAwait(false);
        visualizations.NotifyChanged(context.Scope.UserId, context.Thread.Id);
        var receipt = ChatVisualizationService.CreateReceipt(context.Scope.UserId, context.Thread.Id, Path.GetFileName(context.Path), protector);

        // Without a visible-state line, models repeat show and the chart renders twice.
        return JsonSerializer.Serialize(new { receipt.FileName, receipt.Proof, Status = "Shown to the user." });
    }

    /// <summary>Recovers a bounded source page for a later contextual edit.</summary>
    [DisplayName("read")]
    [AgentTool]
    [Description("Read numbered source lines from an existing encrypted visualization.\nOffset starts at 1. Limit is 1-200 lines, default 100.\nOutput is capped at 50000 source characters. Lines longer than 2000 characters are truncated.\nUse the returned continuation offset for another page." + Preconditions)]
    public async Task<string> ReadAsync(
        [Description(IdDescription)] string id,
        [Description("First line, at least 1.")] int offset = 1,
        [Description("Maximum lines, 1-200. Default 100.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (offset < 1) { throw new ToolFailureException("offset must be at least 1."); }

        if (limit is < 1 or > 200) { throw new ToolFailureException("limit must be between 1 and 200 lines."); }

        var context = await ResolveAsync(id, WorkspacePathAccess.Read, cancellationToken).ConfigureAwait(false);
        return context.Files.ReadFile(context.AgentPath, offset, limit);
    }

    /// <summary>Updates one document using the shared contextual encrypted patch engine.</summary>
    [DisplayName("edit")]
    [AgentTool]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    [Description("Patch an existing visualization and refresh its appearances.\nPatch must contain 1-1048576 UTF-8 bytes of update hunks.\nStart each hunk with @@. Prefix lines with a space for context, - for removal, + for addition.\nDo not include file headers, begin/end markers, moves or other file operations.\nRead current source when context does not match.\nSaved edits remain subject to the show operation's HTML size limit." + Preconditions)]
    public async Task<string> EditAsync(
        [Description(IdDescription)] string id,
        [Description("Contextual update hunks only, starting with @@. No file paths or patch wrapper.")] string patch,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patch) || Encoding.UTF8.GetByteCount(patch) > ChatVisualizationService.MaxHtmlBytes)
        {
            throw new ToolFailureException("patch must contain 1-1048576 UTF-8 bytes of update hunks.");
        }

        var context = await ResolveAsync(id, WorkspacePathAccess.Write, cancellationToken).ConfigureAwait(false);
        var fileName = Path.GetFileName(context.Path);
        // The wrapper supplies the final line break, avoiding an unprefixed blank hunk line.
        var document = $"*** Begin Patch\n*** Update File: {fileName}\n{patch.TrimEnd('\r', '\n')}\n*** End Patch";
        try
        {
            var parsed = PatchParser.Parse(document);
            // Parsing before execution prevents injected headers from reaching another file.
            if (parsed.Operations is not [UpdateFileOperation { MoveTo: null } update] ||
                update.Path != fileName || update.Hunks.Count == 0)
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new ToolFailureException("patch must contain update hunks only.", "Start with @@. Prefix lines with a space, - or +. Omit file headers and begin/end markers.");
        }

        context.Files.ApplyPatch(document, Path.GetDirectoryName(context.Path)!);
        visualizations.NotifyChanged(context.Scope.UserId, context.Thread.Id);
        return JsonSerializer.Serialize(new { id, status = "updated" });
    }

    /// <summary>Deletes one document through existing file permissions and deletion behavior.</summary>
    [DisplayName("delete")]
    [AgentTool]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    [Description("Delete an existing visualization from this thread.\nAll its saved appearances become unavailable.\nThis does not erase source retained in encrypted chat history." + Preconditions)]
    public async Task<string> DeleteAsync(
        [Description(IdDescription)] string id,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(id, WorkspacePathAccess.Write, cancellationToken).ConfigureAwait(false);
        context.Files.ApplyPatch($"*** Begin Patch\n*** Delete File: {Path.GetFileName(context.Path)}\n*** End Patch", Path.GetDirectoryName(context.Path)!);
        visualizations.NotifyChanged(context.Scope.UserId, context.Thread.Id);
        return JsonSerializer.Serialize(new { id, status = "deleted" });
    }

    // IDs resolve under the admitted run, so navigation cannot redirect a mutation to another chat.
    private async Task<(UserServiceScope Scope, ChatThread Thread, string Path, string AgentPath, EncryptedFileToolset Files)> ResolveAsync(
        string id, WorkspacePathAccess access, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
        {
            throw new ToolFailureException("id must contain 1-64 ASCII letters, digits, underscores or hyphens. Use the ID returned by creation, without a path or extension.");
        }

        var run = runs.Current;
        if (run is null || run.ExecutionKind != RunExecutionKind.WebChat ||
            run.Elicitation?.Channel != ElicitationChannel.WebUI || run.DelegationAgentIds.Count > 0)
        {
            throw new ToolFailureException("Visualizations require the directly selected agent in browser chat.");
        }

        var scope = await users.GetOrCreateScopeAsync(run.UserId, ct).ConfigureAwait(false);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(run.UserId, run.ExecutionId, ct).ConfigureAwait(false)
            ?? throw new ToolFailureException("The current chat is unavailable.");
        await visualizations.EnsureAvailableAsync(scope, thread, run.AirGapped, ct).ConfigureAwait(false);
        var folder = attachments.ResolveVisualizationFolderPath(run.UserId, thread.Id, thread.IsIncognito);
        var path = PathHelper.ResolveAuthorizedHostPath(Path.Combine(folder, id + ".html.enc"), run.AllowedDirectories, access, out _, out _);
        // Visualizations live in the user's chat folder, never the shared folder, so the user's key protects them.
        return (scope, thread, path, PathHelper.ResolveToAgentPath(path, run.AllowedDirectories),
            new EncryptedFileToolset(run.UserId, run.UserId, protector, () => run.AllowedDirectories, fileAccess));
    }
}
