namespace Oscova.Agents.Llm;

/// <summary>
/// One model returned by a provider's listing, carrying any metadata the provider can report.
/// Lives here (next to <see cref="IModelProvider"/>) rather than in the Core ApiContracts because
/// it is an <see cref="IModelProvider"/> return type and Core references this project, not the reverse.
/// </summary>
public sealed class ModelDescriptor
{
    /// <summary>
    /// Gets the model identifier (the value stored as <see cref="ModelProviderConfig.ModelId"/>).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the reported context window size, or null when the provider does not expose it.
    /// </summary>
    public int? ContextWindowSize { get; init; }

    /// <summary>
    /// Gets whether the model reports image input support, or null when unknown.
    /// </summary>
    public bool? SupportsImages { get; init; }

    /// <summary>
    /// Gets whether the model reports reasoning/thinking support, or null when unknown.
    /// </summary>
    public bool? SupportsThinking { get; init; }

    /// <summary>
    /// Gets whether the model is present in the provider's local cache, or null when unknown.
    /// </summary>
    public bool? IsInstalled { get; init; }

    /// <summary>
    /// Gets the model license identifier or summary, or null when unavailable.
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// Gets provider-supplied license details, or null when unavailable.
    /// </summary>
    public string? LicenseDescription { get; init; }
}
