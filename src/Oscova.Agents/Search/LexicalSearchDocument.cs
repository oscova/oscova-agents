using System.Collections.Generic;

namespace Oscova.Agents.Search;

/// <summary>One searchable item and the text fields used to rank it lexically.</summary>
public sealed record LexicalSearchDocument(
    string Id,
    string Title,
    string? Description = null,
    IReadOnlyList<string>? Keywords = null);
