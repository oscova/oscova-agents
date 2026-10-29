namespace Oscova.Agents.Core.Logging;

public sealed record LogPage
{
    public required IReadOnlyList<LogEntry> Entries { get; init; }
    public required int TotalCount { get; init; }
    public required int CurrentPage { get; init; }
    public required int TotalPages { get; init; }
    public required string FileName { get; init; }
}
