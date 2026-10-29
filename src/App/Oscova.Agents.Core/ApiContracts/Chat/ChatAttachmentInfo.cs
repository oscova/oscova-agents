using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Public metadata for a staged chat attachment exposed to the web client.</summary>
public sealed class ChatAttachmentInfo
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public AttachmentKind Kind { get; init; }
}
