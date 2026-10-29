using Oscova.Agents.Harness;
using Oscova.Agents.Search;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Shared query, paging, excerpt and clipping rules for the chat and task history tools.</summary>
internal static class HistoryEntryText
{
    /// <summary>Longest entry text returned to the agent, in characters.</summary>
    public const int MaxCharacters = 2000;

    /// <summary>Returns the text cut at <see cref="MaxCharacters"/> and whether it was cut.</summary>
    public static (string Text, bool Truncated) Clip(string text) =>
        text.Length <= MaxCharacters ? (text, false) : (text[..MaxCharacters], true);

    /// <summary>Returns the offset of the next page, or null when this page reaches the end.</summary>
    public static int? NextOffset(int offset, int pageCount, int total) =>
        offset + pageCount < total ? offset + pageCount : null;

    /// <summary>Returns the trimmed query, or null when blank, and fails outside 2 to 200 characters.</summary>
    public static string? NormalizeQuery(string? query)
    {
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (query?.Length is < 2 or > 200)
        {
            throw new ToolFailureException("query must contain 2 to 200 characters after trimming.", "Shorten or expand query to that range.");
        }

        return query;
    }

    public static void ValidatePage(int limit, int offset)
    {
        if (limit is < 1 or > 50)
        {
            throw new ToolFailureException("limit must be 1 to 50.", "Supply limit within that range.");
        }

        if (offset < 0)
        {
            throw new ToolFailureException("offset must be 0 or greater.", "Supply 0 or the previous nextOffset.");
        }
    }

    /// <summary>Builds bounded context around a complete token match in the original text, or null without a match.</summary>
    public static string? CreateExcerpt(string query, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var matches = LexicalSearchStrategy.FindTokenMatches(query, text);
        if (matches.Count == 0)
        {
            return null;
        }

        // Choose evidence before clipping, with room for the complete maximum-length query token.
        var start = Math.Max(0, matches[0].Start - 100);
        return text.Substring(start, Math.Min(400, text.Length - start));
    }
}
