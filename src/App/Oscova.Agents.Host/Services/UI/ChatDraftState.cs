using Microsoft.AspNetCore.Components.Forms;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Definitions;

namespace Oscova.Agents.Host.Services.UI;

/// <summary>Keeps chat drafts, loaded history and reading positions alive for the current Blazor circuit.</summary>
public sealed class ChatDraftState
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<WorkspaceFileReference>> _files = new(StringComparer.Ordinal);

    internal IReadOnlyList<WorkspaceFileReference> TakeFiles(string threadId) =>
        _files.Remove(threadId, out var files) ? files : [];

    internal void SetFiles(string threadId, IReadOnlyList<WorkspaceFileReference> files)
    {
        if (files.Count == 0) { _files.Remove(threadId); }
        else { _files[threadId] = files; }
    }
    private readonly Dictionary<string, IReadOnlyList<(string Id, IBrowserFile File)>> _attachments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _scrollPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentDefinition> _recipients = new(StringComparer.Ordinal);

    /// <summary>Retains loaded display windows alongside their drafts and reading positions.</summary>
    public IDictionary<string, ChatThreadViewResponse> LoadedThreadViews { get; } = new Dictionary<string, ChatThreadViewResponse>(StringComparer.Ordinal);

    /// <summary>Returns the retained reading position for a loaded chat.</summary>
    public double? GetScrollPosition(string threadId) => _scrollPositions.TryGetValue(threadId, out var top) ? top : null;

    /// <summary>Retains the reading position while another chat is selected.</summary>
    public void SetScrollPosition(string threadId, double top) => _scrollPositions[threadId] = top;
    /// <summary>Returns the selected recipient alongside the unsent draft.</summary>
    public AgentDefinition? GetRecipient(string threadId) => _recipients.GetValueOrDefault(threadId);

    /// <summary>Retains recipient identity when switching chats without changing their owners.</summary>
    public void SetRecipient(string threadId, AgentDefinition? recipient)
    {
        if (recipient is null) { _recipients.Remove(threadId); }
        else { _recipients[threadId] = recipient; }
    }

    /// <summary>Retains the active composer's chat identity during route changes.</summary>
    public ChatThread? ActiveThread { get; set; }

    /// <summary>Returns the unsent text for a chat.</summary>
    public string GetText(string threadId) => _texts.GetValueOrDefault(threadId, string.Empty);

    /// <summary>Retains a draft, removing its entry when the composer is cleared.</summary>
    public void SetText(string threadId, string text)
    {
        if (text.Length == 0) { _texts.Remove(threadId); }
        else { _texts[threadId] = text; }
    }

    /// <summary>Transfers retained uploads back to their chat composer.</summary>
    internal IReadOnlyList<(string Id, IBrowserFile File)> TakeAttachments(string threadId) =>
        _attachments.Remove(threadId, out var attachments) ? attachments : [];

    /// <summary>Retains uploaded bytes alongside the ordinary draft during navigation.</summary>
    internal void SetAttachments(string threadId, IReadOnlyList<(string Id, IBrowserFile File)> attachments)
    {
        if (attachments.Count == 0) { _attachments.Remove(threadId); }
        else { _attachments[threadId] = attachments; }
    }

    /// <summary>Reads the picker while it exists so later navigation cannot invalidate an upload.</summary>
    internal static async Task<IBrowserFile> BufferAttachmentAsync(IBrowserFile file, long maxAllowedSize)
    {
        await using var source = file.OpenReadStream(maxAllowedSize);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer).ConfigureAwait(false);
        return new BufferedAttachment(file, buffer.ToArray());
    }

    /// <summary>Keeps selected file metadata and bytes independent of the browser file picker.</summary>
    private sealed class BufferedAttachment(IBrowserFile file, byte[] content) : IBrowserFile
    {
        public string Name { get; } = file.Name;
        public DateTimeOffset LastModified { get; } = file.LastModified;
        public long Size => content.LongLength;
        public string ContentType { get; } = file.ContentType;

        /// <summary>Returns retained bytes after enforcing the caller's size limit.</summary>
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Size > maxAllowedSize) { throw new IOException($"File size {Size} exceeds the allowed size {maxAllowedSize}."); }

            return new MemoryStream(content, writable: false);
        }
    }
}
