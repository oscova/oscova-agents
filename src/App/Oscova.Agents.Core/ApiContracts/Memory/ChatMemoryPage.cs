namespace Oscova.Agents.Core.ApiContracts.Memory;

/// <summary>
/// Carries one page of chat-memory records from the server to the dialog.
/// </summary>
public sealed record ChatMemoryPage
{
    public required IReadOnlyList<ChatMemoryEntry> Entries { get; init; }
    public required int CurrentPage { get; init; }
    public required bool HasNextPage { get; init; }
}
