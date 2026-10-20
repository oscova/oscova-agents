using System;
using Microsoft.ML.Tokenizers;

namespace Oscova.Agents.Utils;

/// <summary>
/// Provides the shared token counter used for agent prompt budgeting and memory chunking.
/// </summary>
public static class AgentTokenCounter
{
    // The tokenizer's byte-pair merge is quadratic in the length of one pre-token, so an unbroken run of
    // letters (for example 40,000 repeated characters) takes about a second. Counting bounded slices keeps
    // the total linear. Slices end before the last space of a whitespace run, which is already a pre-token
    // boundary, so ordinary text counts the same. Only a run longer than the slice gains at most one token per cut.
    private const int MaxSliceLength = 1024;

    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    /// <summary>
    /// Counts tokens using the shared agent text encoding.
    /// </summary>
    public static int CountTokens(string text)
    {
        var count = 0;
        var remaining = text.AsSpan();
        while (remaining.Length > MaxSliceLength)
        {
            var cut = MaxSliceLength - 1;
            while (cut > 0 && (remaining[cut] != ' ' || char.IsWhiteSpace(remaining[cut + 1])))
            {
                cut--;
            }

            if (cut == 0)
            {
                cut = char.IsHighSurrogate(remaining[MaxSliceLength - 1]) ? MaxSliceLength - 1 : MaxSliceLength;
            }

            count += Tokenizer.CountTokens(remaining[..cut]);
            remaining = remaining[cut..];
        }

        return count + Tokenizer.CountTokens(remaining);
    }
}
