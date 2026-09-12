using System.Collections.Generic;
using Oscova.Agents.Search;

namespace Oscova.Agents.Providers;

/// <summary>
/// Scores tools by deterministic token overlap across name, description, and argument names.
/// Adapts tool candidates onto the shared <see cref="LexicalSearchStrategy"/>.
/// </summary>
public sealed class LexicalToolSearchStrategy : IToolSearchStrategy
{
    /// <inheritdoc />
    public IReadOnlyList<string> Search(string query, IReadOnlyList<ToolSearchCandidate> candidates, int maxResults)
    {
        var documents = new List<LexicalSearchDocument>(candidates.Count);
        foreach (var candidate in candidates)
        {
            documents.Add(new LexicalSearchDocument(
                candidate.QualifiedName,
                candidate.QualifiedName,
                candidate.Description,
                candidate.ArgumentNames));
        }

        return LexicalSearchStrategy.Search(query, documents, maxResults);
    }
}
