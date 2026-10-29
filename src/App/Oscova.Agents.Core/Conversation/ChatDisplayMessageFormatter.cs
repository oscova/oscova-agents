using System.Text.Json;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Flattens persisted display messages into transcript text for storage, summaries, and titles.
/// </summary>
public static class ChatDisplayMessageFormatter
{
    /// <summary>Adds selected file addresses to model input while leaving the stored display text untouched.</summary>
    public static string ComposeFiles(string userText, IReadOnlyList<WorkspaceFileReference> files)
    {
        if (files.Count == 0) { return userText; }

        var references = files.Select(file =>
            $"- {JsonSerializer.Serialize(file.Name)} ({JsonSerializer.Serialize(file.Source)}): {JsonSerializer.Serialize(file.AgentPath)}");
        return $"{userText}\n\nSelected workspace files:\n{string.Join("\n", references)}".Trim();
    }

    /// <summary>Creates an initial title within the editable limit, leaving visual truncation to the sidebar.</summary>
    public static string BuildPlaceholderTitle(string text, IReadOnlyList<ChatAttachment> attachments, IReadOnlyList<WorkspaceFileReference>? workspaceFiles = null)
    {
        // Attachment-only messages still need a recognizable title in the conversation list.
        if (string.IsNullOrWhiteSpace(text)) { text = attachments.FirstOrDefault()?.DisplayName ?? workspaceFiles?.FirstOrDefault()?.Name ?? text; }

        return text.Length > RenameThreadRequest.MaxTitleLength
            ? text[..RenameThreadRequest.MaxTitleLength].TrimEnd()
            : text;
    }

    /// <summary>
    /// Returns the primary conversational text for a message.
    /// For assistant messages, this reads text segments because <see cref="ChatDisplayMessage.Text"/> is usually empty by design.
    /// </summary>
    public static string GetPrimaryText(ChatDisplayMessage message)
    {
        return GetMessageText(message, includeNonTextSegments: false);
    }

    /// <summary>
    /// Returns a storage-oriented transcript string that keeps conversational text and sanitized tool activity.
    /// </summary>
    public static string GetStorageText(ChatDisplayMessage message)
    {
        return GetMessageText(message, includeNonTextSegments: true);
    }

    /// <summary>
    /// Formats a single transcript line with the speaker prefix expected by indexing and summarization.
    /// </summary>
    public static string GetConversationLine(ChatDisplayMessage message, bool includeNonTextSegments = false)
    {
        var content = includeNonTextSegments
            ? GetStorageText(message)
            : GetPrimaryText(message);

        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var speaker = message.From == MessageOwner.User ? "User" : "Assistant";
        return $"{speaker}: {content}";
    }

    private static string GetMessageText(ChatDisplayMessage message, bool includeNonTextSegments)
    {
        var segmentParts = message.Segments
            .Select(segment => FormatSegment(segment, includeNonTextSegments))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        // Assistant output is streamed into segments, so prefer segment content whenever it exists.
        if (segmentParts.Count > 0)
        {
            return string.Join(Environment.NewLine + Environment.NewLine, segmentParts);
        }

        return Normalize(message.Text);
    }

    private static string FormatSegment(ChatSegment segment, bool includeNonTextSegments)
    {
        return segment.Type switch
        {
            ChatSegmentType.Text => Normalize(segment.Text),
            // Reasoning text can include sensitive intermediate model output, so storage paths skip it.
            ChatSegmentType.Think when includeNonTextSegments => string.Empty,
            // Storage keeps the tool name for traceability without persisting serialized arguments.
            ChatSegmentType.ToolCall when includeNonTextSegments && !string.IsNullOrWhiteSpace(segment.FunctionCall?.Name)
                => $"[Tool Call]{Environment.NewLine}{segment.FunctionCall!.Name!.Trim()}",
            ChatSegmentType.ToolCall when includeNonTextSegments => "[Tool Call]",
            _ => string.Empty
        };
    }

    private static string Normalize(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
    }
}
