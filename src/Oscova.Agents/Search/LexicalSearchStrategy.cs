using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Oscova.Agents.Search;

/// <summary>
/// Ranks documents by deterministic token overlap across title, description, and keywords.
/// Shared so search surfaces do not each grow their own scorer.
/// </summary>
public static class LexicalSearchStrategy
{
    private const int TitleWeight = 2;
    private const int BodyWeight = 1;

    /// <summary>Returns the ids of the best matches, most relevant first, capped to <paramref name="maxResults"/>.</summary>
    public static IReadOnlyList<string> Search(
        string query,
        IReadOnlyList<LexicalSearchDocument> documents,
        int maxResults)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0 || documents.Count == 0 || maxResults <= 0)
        {
            return [];
        }

        var scored = new List<(string Id, int Score)>();
        foreach (var document in documents)
        {
            var titleTokens = Tokenize(document.Title);
            var bodyTokens = Tokenize(document.Description ?? string.Empty);
            if (document.Keywords is not null)
            {
                foreach (var keyword in document.Keywords)
                {
                    bodyTokens.UnionWith(Tokenize(keyword));
                }
            }

            var score = 0;
            foreach (var token in queryTokens)
            {
                // Highest-weight field wins per token; a title hit does not also add the body score.
                if (titleTokens.Contains(token))
                {
                    score += TitleWeight;
                    continue;
                }

                if (bodyTokens.Contains(token))
                {
                    score += BodyWeight;
                }
            }

            if (score > 0)
            {
                scored.Add((document.Id, score));
            }
        }

        return [.. scored
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(entry => entry.Id)];
    }

    // Splits text into lowercase tokens, breaking on non-alphanumerics and camelCase boundaries.
    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();

        char previous = '\0';
        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (builder.Length > 0 && char.IsUpper(character) && char.IsLower(previous))
                {
                    AddToken(tokens, builder);
                }

                builder.Append(char.ToLowerInvariant(character));
                previous = character;
                continue;
            }

            AddToken(tokens, builder);
            previous = '\0';
        }

        AddToken(tokens, builder);
        return tokens;
    }

    private static void AddToken(HashSet<string> tokens, StringBuilder builder)
    {
        if (builder.Length >= 2)
        {
            tokens.Add(builder.ToString());
        }

        builder.Clear();
    }
}
