using System.Text.Json.Serialization;

namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Describes an estimated chat context and its fit within the selected model window.</summary>
public sealed class ChatContextPreflightResponse
{
    /// <summary>
    /// Gets or sets whether the selected provider has a known context window size.
    /// </summary>
    public bool IsContextWindowKnown { get; init; }

    /// <summary>
    /// Gets or sets whether the estimated prompt plus output reserve exceeds the window.
    /// </summary>
    public bool IsOverBudget { get; init; }

    /// <summary>
    /// Gets or sets the configured context window size.
    /// </summary>
    public int ContextWindowSize { get; init; }

    /// <summary>
    /// Gets or sets the estimated input tokens that can fit after reserving output tokens.
    /// </summary>
    public int AvailableInputTokens { get; init; }

    /// <summary>
    /// Gets or sets the estimated input tokens for the pending request.
    /// </summary>
    public int EstimatedInputTokens { get; init; }

    /// <summary>Gets the estimated percentage of the full context window used by input.</summary>
    [JsonIgnore]
    public int EstimatedUsagePercentage => ContextWindowSize <= 0
        ? 0
        : (int)Math.Round(
            EstimatedInputTokens * 100d / ContextWindowSize,
            MidpointRounding.AwayFromZero);

    /// <summary>Gets the estimated input tokens still free after the output reserve.</summary>
    [JsonIgnore]
    public int EstimatedFreeInputTokens => Math.Max(0, AvailableInputTokens - EstimatedInputTokens);

    /// <summary>Gets the percentage of the full window still free for input.</summary>
    [JsonIgnore]
    public int EstimatedFreeInputPercentage => ContextWindowSize <= 0
        ? 0
        : (int)Math.Round(
            EstimatedFreeInputTokens * 100d / ContextWindowSize,
            MidpointRounding.AwayFromZero);

    /// <summary>
    /// Gets or sets the reserved output tokens used for the fit check.
    /// </summary>
    public int ReservedOutputTokens { get; init; }

    /// <summary>
    /// Gets or sets how far the estimate exceeds the available input budget.
    /// </summary>
    public int OverageTokens { get; init; }

    /// <summary>
    /// Gets or sets the resolved provider display name.
    /// </summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the resolved model identifier, when configured.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Gets or sets the category-level estimate breakdown.
    /// </summary>
    public IReadOnlyList<ChatContextPreflightBreakdownItem> Breakdown { get; init; } = [];
}
