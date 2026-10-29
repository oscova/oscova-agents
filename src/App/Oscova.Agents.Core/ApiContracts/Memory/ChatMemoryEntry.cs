namespace Oscova.Agents.Core.ApiContracts.Memory;

/// <summary>
/// Represents one stored chat-memory chunk as displayed in the UI inspector.
/// </summary>
public sealed record ChatMemoryEntry
{
    public required string Key { get; init; }
    public required string AgentId { get; init; }
    public required string ThreadId { get; init; }
    public required string Content { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required int ChunkIndex { get; init; }
}
