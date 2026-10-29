namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>
/// A single session todo item surfaced to the chat UI.
/// </summary>
public sealed record ChatTodoItem(int Id, string Title, string? Description, bool IsComplete);
