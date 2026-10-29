using Oscova.Agents.Llm;

namespace Oscova.Agents.Core.ApiContracts.ModelProviders;

/// <summary>Model provider settings and available model lists; used for both GET and PUT.</summary>
public sealed class ModelProviderConfiguration
{
    public bool CanManageCatalog { get; set; }
    public IReadOnlyList<Guid> SharedProviderIds { get; set; } = [];
    public IReadOnlyDictionary<Guid, ModelBudgetPolicy> Budgets { get; set; } = new Dictionary<Guid, ModelBudgetPolicy>();
    public bool ConfirmInterruptions { get; set; }
    public Guid? OperatorDefaultChatModelProviderId { get; set; }
    public Guid? OperatorDefaultEmbeddingModelProviderId { get; set; }
    public IReadOnlyList<Guid> EmbeddingProviderIds { get; set; } = [];
    public IList<ModelProviderConfig> ModelProviders { get; set; } = [];
    public Guid? SelectedChatModelProviderId { get; set; }
    public Guid? EmbeddingModelProviderId { get; set; }
    public Guid? FallbackChatModelProviderId { get; set; }
    public IReadOnlyList<string> LocalModels { get; set; } = [];
    public IReadOnlyList<string> SupportedEmbeddingTypes { get; set; } = [];
    public IReadOnlyList<string> AvailableProviderTypes { get; set; } = [];
    public IReadOnlyList<ProviderTypeDefault> ProviderTypeDefaults { get; set; } = [];
}
