using Microsoft.ML.Tokenizers;

namespace Oscova.Agents.Utils;

/// <summary>
/// Provides the shared token counter used for agent prompt budgeting and memory chunking.
/// </summary>
public static class AgentTokenCounter
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    /// <summary>
    /// Counts tokens using the shared agent text encoding.
    /// </summary>
    public static int CountTokens(string text)
    {
        return Tokenizer.CountTokens(text);
    }
}
