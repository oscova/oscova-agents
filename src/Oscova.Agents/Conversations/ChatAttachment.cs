using System;

namespace Oscova.Agents.Conversations;

/// <summary>Describes a staged chat attachment persisted with its message.</summary>
public sealed class ChatAttachment
{
    /// <summary>Gets the attachment identifier.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Gets the file name displayed to the user.</summary>
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>Gets the staged file name used for storage.</summary>
    public string StoredFileName { get; init; } = string.Empty;
    /// <summary>Gets the attachment media type.</summary>
    public string MediaType { get; init; } = string.Empty;
    /// <summary>Gets the attachment size in bytes.</summary>
    public long SizeBytes { get; init; }
    /// <summary>Gets the attachment category.</summary>
    public AttachmentKind Kind { get; init; }
    /// <summary>Gets the staged path relative to the attachment root.</summary>
    public string RelativePath { get; init; } = string.Empty;
    /// <summary>Gets whether the attachment is staged outside the user's data directory.</summary>
    public bool IsEphemeral { get; init; }
}
