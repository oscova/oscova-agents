namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Raised when a chat attachment fails extension/content-type/size validation.
/// Message is safe to surface to the user (callers return it as a 400).
/// </summary>
public sealed class ChatAttachmentValidationException(string message) : Exception(message);
