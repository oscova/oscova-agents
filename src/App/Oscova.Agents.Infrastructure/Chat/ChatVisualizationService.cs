using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Secrets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Validates encrypted chat documents for publication and authenticated inline rendering.</summary>
public sealed class ChatVisualizationService(IChatAttachmentService attachments, IContentProtector protector,
    ILogger<ChatVisualizationService> logger)
{
    public const int MaxHtmlBytes = 1048576;
    public const int MaxEncryptedBytes = 2097152;

    /// <summary>Retains only a thread-relative filename in the encrypted tool result.</summary>
    public sealed record Receipt(string FileName, string Proof);

    /// <summary>Binds publication to its user, thread and document without a second persistence store.</summary>
    public static Receipt CreateReceipt(string userId, string threadId, string fileName, IContentProtector protector) =>
        // A URL-safe alphabet remains unchanged inside nested JSON checkpoint strings.
        new(fileName, protector.Protect(userId, "visualization-publication", threadId + "/" + fileName).Replace('+', '-').Replace('/', '_'));

    /// <summary>Rejects fabricated references and receipts copied from another thread.</summary>
    public static bool IsAuthentic(string userId, string threadId, Receipt receipt, IContentProtector protector) =>
        !string.IsNullOrWhiteSpace(receipt.Proof) &&
        protector.Unprotect(userId, "visualization-publication", receipt.Proof.Replace('-', '+').Replace('_', '/')) == threadId + "/" + receipt.FileName;

    /// <summary>Revalidates a publication before opening its document for the authenticated component.</summary>
    public async Task<string> ReadPublishedAsync(UserServiceScope scope, ChatThread thread, Receipt receipt,
        CancellationToken ct = default)
    {
        await EnsureAvailableAsync(scope, thread, ct: ct).ConfigureAwait(false);
        if (!IsAuthentic(scope.UserId, thread.Id, receipt, protector))
        {
            throw new ToolFailureException("Visualization publication is invalid for this chat.");
        }

        var path = Path.Combine(attachments.ResolveVisualizationFolderPath(scope.UserId, thread.Id, thread.IsIncognito), receipt.FileName);
        return await ReadFileAsync(scope.UserId, thread, path, path, ct).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0046", Justification = "Refresh is scoped by user and thread identities.")]
    public event Action<string, string>? Changed;

    /// <summary>Refreshes appearances of a document after successful publication.</summary>
    public void NotifyChanged(string userId, string threadId) => Changed?.Invoke(userId, threadId);

    /// <summary>Recognizes only a matched successful publication tool result.</summary>
    public static bool TryRead(ChatSegment segment, out Receipt? receipt)
    {
        receipt = null;
        if (segment.Type != ChatSegmentType.ToolCall || segment.FunctionCall?.Name != VisualizationToolset.QualifiedToolName ||
            segment.FunctionResult is not { } result || result.CallId != segment.FunctionCall.CallId ||
            segment.ExecutionState != ToolExecutionState.Completed)
        {
            return false;
        }

        try
        {
            var json = result.Result switch { string text => text, JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(), _ => null };
            receipt = json is null ? null : JsonSerializer.Deserialize<Receipt>(json);
            return receipt is not null && PathHelper.IsSafePathSegment(receipt.FileName) &&
                receipt.FileName.EndsWith(".html.enc", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Rechecks current privacy policy instead of trusting the policy at publication time.</summary>
    public async Task EnsureAvailableAsync(UserServiceScope scope, ChatThread thread, bool ambientLocalOnly = false,
        CancellationToken ct = default)
    {
        var agent = (thread.AgentId is null
            ? await scope.AgentDefinitionStore.GetMainAgentAsync(scope.UserId, ct).ConfigureAwait(false)
            : await scope.AgentDefinitionStore.GetAgentAsync(scope.UserId, thread.AgentId, ct).ConfigureAwait(false))
            ?? throw new ToolFailureException("The visualization's agent is unavailable.");
        if (AgentRunScopeFactory.RequiresLocalExecution(ambientLocalOnly, scope.PrivacySettings.IsLocalOnly, agent.IsLocalOnly))
        {
            throw new ToolFailureException("Visualizations are disabled in local-only mode.");
        }

        if (!protector.IsAvailable(scope.UserId))
        {
            throw new ToolFailureException("Unlock your vault before viewing visualizations.");
        }
    }

    /// <summary>Reads contained ciphertext and decrypts in memory without a plaintext preview file.</summary>
    public async Task<string> ReadFileAsync(string userId, ChatThread thread, string path, string agentPath,
        CancellationToken ct = default)
    {
        var folder = attachments.ResolveVisualizationFolderPath(userId, thread.Id, thread.IsIncognito);
        if (!PathHelper.ArePathsEqual(Path.GetDirectoryName(path)!, folder))
        {
            throw new ToolFailureException("file_path must be directly inside this thread's visualization directory.");
        }

        if (!protector.IsAvailable(userId)) { throw new ToolFailureException("Unlock your vault before viewing visualizations."); }

        try
        {
            var files = new WorkspaceFileAccess(() => [new AllowedDirectory(folder, ChatVisualizationFiles.DirectoryLabel,
                DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)]);
            using var stream = files.OpenRead(path);
            if (stream.Length > MaxEncryptedBytes) { throw new ToolFailureException("Encrypted visualization exceeds 2097152 bytes. Reduce its size."); }

            using var reader = new StreamReader(stream);
            var ciphertext = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            // Failed decryption alone never means plaintext, so damaged encrypted data is retained.
            if (Regex.IsMatch(ciphertext, @"^\s*<(?:!doctype\s+html\b|!--|[a-z][a-z0-9]*(?:\s|>))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            {
                throw new ToolFailureException("file_path contains unencrypted HTML.",
                    $"Delete the plaintext file '{agentPath}' using the existing file deletion tools and permissions. Recreate it using visualization creation, then call show with the returned ID. Renaming it is not encryption.");
            }

            if (!path.EndsWith(".html.enc", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolFailureException("file_path must end with .html.enc. Create it using encrypted file tools.");
            }

            var html = protector.Unprotect(userId, EncryptedFileContent.ProtectionPurpose, ciphertext)
                ?? throw new ToolFailureException("The visualization could not be decrypted. Check vault access and recreate damaged content using encrypted file tools.");
            if (string.IsNullOrWhiteSpace(html) || Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes)
            {
                throw new ToolFailureException("Decrypted visualization must contain 1-1048576 UTF-8 bytes. Reduce or populate the HTML.");
            }

            return html;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Visualization read failed for user {UserId}, thread {ThreadId}", userId, thread.Id);
            throw new ToolFailureException("Visualization unavailable. Check that the encrypted file exists in this thread's directory.");
        }
    }
}
