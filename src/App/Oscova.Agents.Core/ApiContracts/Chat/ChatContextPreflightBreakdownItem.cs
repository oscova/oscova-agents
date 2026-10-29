namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>
/// Reports one estimated contributor to a chat context-window preflight result.
/// </summary>
public sealed class ChatContextPreflightBreakdownItem
{
    /// <summary>
    /// Gets or sets the stable category key used by the UI for localization.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the estimated token count for this category.
    /// </summary>
    public int EstimatedTokens { get; init; }
}
