namespace Oscova.Agents.Core.Interfaces;

/// <summary>Separates unavailable semantic search from a successful search with no matches.</summary>
public sealed record SemanticSearchResult<T>(IReadOnlyList<T> Matches, string? UnavailableReason = null);
