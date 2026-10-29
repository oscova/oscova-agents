namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>
/// Describes a browser-side pending attachment without transferring file bytes.
/// </summary>
public sealed class PendingChatAttachmentSummary
{
    /// <summary>
    /// Gets or sets the file name selected by the user.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the browser-reported media type, when known.
    /// </summary>
    public string MediaType { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long SizeBytes { get; init; }
}
