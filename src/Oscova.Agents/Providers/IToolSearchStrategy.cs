using System.Collections.Generic;

namespace Oscova.Agents.Providers;

/// <summary>Represents one searchable tool and the text used to match it.</summary>
public sealed record ToolSearchCandidate(
    string QualifiedName,
    string Description,
    IReadOnlyList<string> ArgumentNames);

/// <summary>Ranks tool candidates against a free-text query.</summary>
public interface IToolSearchStrategy
{
    /// <summary>Returns the qualified names of the best matches, most relevant first.</summary>
    IReadOnlyList<string> Search(string query, IReadOnlyList<ToolSearchCandidate> candidates, int maxResults);
}
