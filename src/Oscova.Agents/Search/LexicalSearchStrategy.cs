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

    /// <summary>Returns original-text positions of complete query tokens using the search tokenizer.</summary>
    public static IReadOnlyList<(int Start, int Length)> FindTokenMatches(string query, string text)
    {
        var queryTokens = Tokenize(query);
        return GetTokenSpans(text)
            .Where(span => queryTokens.Contains(NormalizeToken(text, span.Start, span.Length)))
            .ToArray();
    }

    // Ranking and evidence use one tokenizer so clipping cannot invent a keyword match.
    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var span in GetTokenSpans(text))
        {
            tokens.Add(NormalizeToken(text, span.Start, span.Length));
        }

        return tokens;
    }

    /// <summary>Finds the same alphanumeric and camel-case token boundaries used for ranking.</summary>
    private static IEnumerable<(int Start, int Length)> GetTokenSpans(string text)
    {
        var start = -1;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (!char.IsLetterOrDigit(character))
            {
                if (start >= 0 && index - start >= 2)
                {
                    yield return (start, index - start);
                }
                start = -1;
                continue;
            }

            if (start >= 0 && char.IsUpper(character) && char.IsLower(text[index - 1]))
            {
                if (index - start >= 2)
                {
                    yield return (start, index - start);
                }
                start = index;
            }
            else if (start < 0)
            {
                start = index;
            }
        }

        if (start >= 0 && text.Length - start >= 2)
        {
            yield return (start, text.Length - start);
        }
    }

    private static string NormalizeToken(string text, int start, int length)
    {
        var builder = new StringBuilder(length);
        for (var index = start; index < start + length; index++)
        {
            builder.Append(char.ToLowerInvariant(text[index]));
        }

        // Drop a plural "s" so "tab" matches "tabs"; "ss" endings such as "process" stay whole.
        if (builder.Length > 3 && builder[^1] == 's' && builder[^2] != 's')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
