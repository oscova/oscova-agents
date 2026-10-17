namespace Oscova.Agents.Llm;

/// <summary>Reports model acquisition progress for transfers and stages without a byte total.</summary>
public sealed class ModelDownloadProgress
{
    /// <summary>Gets the current transfer percentage, or null when progress is indeterminate.</summary>
    public double? Percent { get; init; }

    /// <summary>Gets the provider's current transfer or preparation status, when available.</summary>
    public string? Status { get; init; }
}
