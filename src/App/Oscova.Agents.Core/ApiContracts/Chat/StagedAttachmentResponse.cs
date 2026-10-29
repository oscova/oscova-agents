using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.ApiContracts.Chat;

public sealed record StagedAttachmentResponse(
    string Id,
    string DisplayName,
    string MediaType,
    long SizeBytes,
    AttachmentKind Kind)
{
    public static StagedAttachmentResponse FromAttachment(ChatAttachment attachment) =>
        new(attachment.Id, attachment.DisplayName, attachment.MediaType, attachment.SizeBytes, attachment.Kind);
}
