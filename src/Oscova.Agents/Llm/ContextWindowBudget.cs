using System;

namespace Oscova.Agents.Llm;

/// <summary>
/// Centralizes context-window budgeting decisions shared by reducers and chat preflight checks.
/// </summary>
public static class ContextWindowBudget
{
    /// <summary>
    /// Historical default history budget used when the provider context window is unknown.
    /// </summary>
    public const int DefaultHistoryBudget = 8192;

    /// <summary>
    /// Lowest derived history budget allowed for small-context models.
    /// </summary>
    public const int MinimumHistoryBudget = 2048;

    /// <summary>
    /// Maximum automatic output reserve used when a model does not declare an output token limit.
    /// </summary>
    public const int MaximumOutputReserve = 4096;

    private const double HistoryBudgetShare = 0.6;

    /// <summary>
    /// Derives the output token reserve for a context-window fit check.
    /// </summary>
    public static int DeriveOutputReserve(int contextWindowSize, int? maxOutputTokens)
    {
        if (contextWindowSize <= 0)
        {
            return 0;
        }

        if (maxOutputTokens is > 0)
        {
            return Math.Min(maxOutputTokens.Value, contextWindowSize);
        }

        return Math.Min(contextWindowSize / 4, MaximumOutputReserve);
    }

    /// <summary>
    /// Derives the history token budget from a model's context window.
    /// </summary>
    public static int DeriveHistoryBudget(int contextWindowSize, int? maxOutputTokens)
    {
        if (contextWindowSize <= 0)
        {
            return DefaultHistoryBudget;
        }

        var outputReserve = DeriveOutputReserve(contextWindowSize, maxOutputTokens);
        return Math.Max(MinimumHistoryBudget, (int)((contextWindowSize - outputReserve) * HistoryBudgetShare));
    }
}
