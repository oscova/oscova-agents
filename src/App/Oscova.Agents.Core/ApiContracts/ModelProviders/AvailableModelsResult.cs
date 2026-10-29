using Oscova.Agents.Llm;

namespace Oscova.Agents.Core.ApiContracts.ModelProviders;

/// <summary>Result of fetching available models (with any provider-reported metadata) from a provider.</summary>
public sealed class AvailableModelsResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public IList<ModelDescriptor> Models { get; set; } = [];
}
