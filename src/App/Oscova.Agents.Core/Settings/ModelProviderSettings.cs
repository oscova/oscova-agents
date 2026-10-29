using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;
using Oscova.Agents.Llm;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.ApiContracts.ModelProviders;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Model provider configuration for providers and the selected chat, embedding, and fallback providers.
/// </summary>
public class ModelProviderSettings : SettingsBase
{
    public const string Key = "llm";

    private ObservableCollection<ModelProviderConfig> _providers = [];
    private Guid? _selectedProviderId;
    private Guid? _embeddingModelProviderId;
    private Guid? _fallbackProviderId;

    private ModelProviderSettings? _operatorCatalog;
    private IUserPermissionService? _permissions;

    /// <summary>Identifies the account whose selections and provider operations use these settings.</summary>
    public string UserId { get; }

    /// <summary>Holds budget policies only for this account's provider definitions.</summary>
    public IReadOnlyDictionary<Guid, ModelBudgetPolicy> Budgets { get; set; } = new Dictionary<Guid, ModelBudgetPolicy>();

    /// <summary>Remembers the last daily and monthly alert period for each owned provider.</summary>
    public IDictionary<string, DateTimeOffset> BudgetAlertPeriods { get; set; } = new Dictionary<string, DateTimeOffset>();

    /// <summary>Reports whether this account consumes the operator's provider definitions.</summary>
    public bool UsesOperatorCatalog => _operatorCatalog is not null;

    /// <summary>Gets only definitions owned by this account for authorized editing and persistence.</summary>
    public IReadOnlyList<ModelProviderConfig> OwnedProviders => _providers;

    /// <summary>Gets operator definitions without copying their private settings into a consumer's file.</summary>
    public IReadOnlyList<ModelProviderConfig> SharedProviders => _operatorCatalog?.OwnedProviders ?? [];

    /// <summary>Gets the embedding identities owned by this account.</summary>
    public IReadOnlyList<Guid> OwnedEmbeddingProviderIds => _embeddingProviderIds;

    /// <summary>Resolves the configuration owner for credentials and shared budget accounting.</summary>
    public ModelProviderSettings OwnerOf(Guid providerId) =>
        _operatorCatalog is not null && SharedProviders.Any(provider => provider.Id == providerId)
            ? _operatorCatalog : this;

    /// <summary>Gets the personal chat selection, with null meaning the operator default.</summary>
    public Guid? ChatProviderOverrideId => _selectedProviderId;

    /// <summary>Gets the personal embedding selection, with null meaning the operator default.</summary>
    public Guid? EmbeddingProviderOverrideId => _embeddingModelProviderId;

    /// <summary>Gets the personal fallback selection, with null meaning no fallback.</summary>
    public Guid? FallbackProviderOverrideId => _fallbackProviderId;

    /// <summary>Gets the inherited chat default independently of a personal override.</summary>
    public Guid? OperatorDefaultChatModelProviderId => _operatorCatalog?.SelectedChatModelProviderId;

    /// <summary>Gets the inherited embedding default independently of a personal override.</summary>
    public Guid? OperatorDefaultEmbeddingModelProviderId => _operatorCatalog?.EmbeddingModelProviderId;

    /// <summary>Shares the unlocked catalog without copying provider definitions into this user's settings.</summary>
    public void BindOperatorCatalog(ModelProviderSettings catalog, IUserPermissionService permissions)
    {
        if (ReferenceEquals(_operatorCatalog, catalog)) { return; }

        _operatorCatalog = catalog;
        _permissions = permissions;
        OnChanged();
    }

    /// <summary>Rechecks a personal provider's operator grant before opening or using its client.</summary>
    public Task RequireAccessAsync(Guid providerId, CancellationToken ct = default) =>
        UsesOperatorCatalog && OwnerOf(providerId) == this && _permissions is not null
            ? _permissions.RequireAsync(UserId, UserPermissionKeys.ManagePersonalModelProviders, ct)
            : Task.CompletedTask;
    private IReadOnlyList<Guid> _embeddingProviderIds = [];

    /// <summary>Identifies catalog entries offered by this application's embedding selector.</summary>
    public IReadOnlyList<Guid> EmbeddingProviderIds
    {
        get => _operatorCatalog is null ? _embeddingProviderIds : [.. _operatorCatalog.EmbeddingProviderIds, .. _embeddingProviderIds];
        set => SetProperty(ref _embeddingProviderIds, value.ToArray());
    }

    public ModelProviderSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
        : base(repository, userId, loggerFactory)
    {
        UserId = userId;
        _providers.CollectionChanged += ProvidersOnCollectionChanged;
    }

    public override string SettingsKey => Key;

    public ObservableCollection<ModelProviderConfig> ModelProviderConfigs
    {
        get => _operatorCatalog is null ? _providers : new([.. SharedProviders, .. _providers]);
        set
        {
            if (ReferenceEquals(_providers, value))
            {
                return;
            }

            _providers.CollectionChanged -= ProvidersOnCollectionChanged;
            _providers = value ?? [];
            _providers.CollectionChanged += ProvidersOnCollectionChanged;

            OnPropertyChanged();
            OnChanged();
        }
    }

    public Guid? SelectedChatModelProviderId
    {
        get => _selectedProviderId ?? _operatorCatalog?.SelectedChatModelProviderId;
        set => SetProperty(ref _selectedProviderId, value);
    }

    public ModelProviderConfig? SelectedChatModelProvider =>
        SelectedChatModelProviderId.HasValue
            ? ModelProviderConfigs.FirstOrDefault(p => p.Id == SelectedChatModelProviderId.Value && !EmbeddingProviderIds.Contains(p.Id))
            : null;

    /// <summary>
    /// Which provider config to use for generating embeddings.
    /// </summary>
    public Guid? EmbeddingModelProviderId
    {
        get => _embeddingModelProviderId ?? _operatorCatalog?.EmbeddingModelProviderId;
        set => SetProperty(ref _embeddingModelProviderId, value);
    }

    /// <summary>
    /// Resolved config for the selected embedding provider, or null if not configured.
    /// </summary>
    public ModelProviderConfig? SelectedEmbeddingModelProvider =>
        EmbeddingModelProviderId.HasValue
            ? ModelProviderConfigs.FirstOrDefault(p => p.Id == EmbeddingModelProviderId.Value && EmbeddingProviderIds.Contains(p.Id))
            : null;

    /// <summary>
    /// Which provider config to use when the default chat provider fails.
    /// </summary>
    public Guid? FallbackChatModelProviderId
    {
        get => _fallbackProviderId;
        set => SetProperty(ref _fallbackProviderId, value);
    }

    /// <summary>
    /// Resolved config for the selected fallback provider, or null if not configured.
    /// </summary>
    public ModelProviderConfig? SelectedFallbackChatModelProvider =>
        FallbackChatModelProviderId.HasValue
            ? ModelProviderConfigs.FirstOrDefault(p => p.Id == FallbackChatModelProviderId.Value && !EmbeddingProviderIds.Contains(p.Id))
            : null;

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            // Shared definitions remain in the operator's encrypted settings file.
            ModelProviders = _providers.Select(ToFile).ToList(),
            EmbeddingProviderIds = _embeddingProviderIds.ToList(),
            Budgets = Budgets.ToDictionary(),
            BudgetAlertPeriods = BudgetAlertPeriods.ToDictionary(),
            SelectedModelProviderId = _selectedProviderId,
            EmbeddingModelProviderId = _embeddingModelProviderId,
            FallbackChatModelProviderId = _fallbackProviderId
        });
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is null) { return; }

        ModelProviderConfigs = data.ModelProviders is null ? []
            : new ObservableCollection<ModelProviderConfig>(data.ModelProviders.Select(ToConfig));
        EmbeddingProviderIds = data.EmbeddingProviderIds ?? [];
        Budgets = data.Budgets ?? [];
        BudgetAlertPeriods = data.BudgetAlertPeriods ?? [];
        // Missing selections remain repairable instead of silently inheriting another provider.
        SelectedChatModelProviderId = data.SelectedModelProviderId;
        EmbeddingModelProviderId = data.EmbeddingModelProviderId;
        FallbackChatModelProviderId = data.FallbackChatModelProviderId;
    }

    private void ProvidersOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnChanged();
    }

    private static ModelProviderConfigFile ToFile(ModelProviderConfig config)
    {
        return new ModelProviderConfigFile
        {
            Id = config.Id,
            Name = config.Name,
            Type = config.Type,
            BaseUrl = config.BaseUrl,
            IsLocalEndpoint = config.IsLocalEndpoint,
            ModelId = config.ModelId,
            CredentialKey = config.CredentialKey,
            Options = config.Options is null ? [] : new Dictionary<string, string>(config.Options, StringComparer.Ordinal),
            SupportsImages = config.SupportsImages,
            ContextWindowSize = config.ContextWindowSize,
            InputCostPerMillionTokens = config.InputCostPerMillionTokens,
            OutputCostPerMillionTokens = config.OutputCostPerMillionTokens,
            OllamaKeepAlive = string.IsNullOrWhiteSpace(config.OllamaKeepAlive)
                ? null
                : config.OllamaKeepAlive.Trim(),
            SupportsThinking = config.SupportsThinking,
            VectorDimensions = config.VectorDimensions,
            ChatOptions = new ChatOptionsFile
            {
                Temperature = config.ChatOptions.Temperature,
                TopP = config.ChatOptions.TopP,
                FrequencyPenalty = config.ChatOptions.FrequencyPenalty,
                PresencePenalty = config.ChatOptions.PresencePenalty,
                TopK = config.ChatOptions.TopK,
                MaxOutputTokens = config.ChatOptions.MaxOutputTokens,
                Seed = config.ChatOptions.Seed,
                ReasoningEffort = config.ChatOptions.Reasoning?.Effort,
                AllowMultipleToolCalls = config.ChatOptions.AllowMultipleToolCalls
            }
        };
    }

    private static ModelProviderConfig ToConfig(ModelProviderConfigFile file)
    {
        return new ModelProviderConfig
        {
            Id = file.Id,
            Name = file.Name,
            Type = file.Type,
            BaseUrl = file.BaseUrl,
            IsLocalEndpoint = file.IsLocalEndpoint,
            ModelId = file.ModelId,
            CredentialKey = file.CredentialKey,
            Options = file.Options is null ? [] : new Dictionary<string, string>(file.Options, StringComparer.Ordinal),
            SupportsImages = file.SupportsImages,
            ContextWindowSize = file.ContextWindowSize,
            InputCostPerMillionTokens = file.InputCostPerMillionTokens,
            OutputCostPerMillionTokens = file.OutputCostPerMillionTokens,
            OllamaKeepAlive = string.IsNullOrWhiteSpace(file.OllamaKeepAlive)
                ? null
                : file.OllamaKeepAlive.Trim(),
            SupportsThinking = file.SupportsThinking,
            VectorDimensions = file.VectorDimensions,
            ChatOptions = new ChatOptions
            {
                Temperature = file.ChatOptions.Temperature,
                TopP = file.ChatOptions.TopP,
                FrequencyPenalty = file.ChatOptions.FrequencyPenalty,
                PresencePenalty = file.ChatOptions.PresencePenalty,
                TopK = file.ChatOptions.TopK,
                MaxOutputTokens = file.ChatOptions.MaxOutputTokens,
                Seed = file.ChatOptions.Seed,
                // Missing effort preserves the provider's existing Default behavior.
                Reasoning = file.ChatOptions.ReasoningEffort is { } effort ? new ReasoningOptions { Effort = effort } : null,
                AllowMultipleToolCalls = file.ChatOptions.AllowMultipleToolCalls
            }
        };
    }

    private sealed class Snapshot
    {
        public Dictionary<Guid, ModelBudgetPolicy>? Budgets { get; set; }
        public Dictionary<string, DateTimeOffset>? BudgetAlertPeriods { get; set; }
        public List<Guid>? EmbeddingProviderIds { get; set; }
        public List<ModelProviderConfigFile>? ModelProviders { get; set; }
        public Guid? SelectedModelProviderId { get; set; }
        public Guid? EmbeddingModelProviderId { get; set; }
        public Guid? FallbackChatModelProviderId { get; set; }
    }

    private sealed class ModelProviderConfigFile
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = ModelProviderKeys.Ollama;
        public string? BaseUrl { get; set; }
        public bool? IsLocalEndpoint { get; set; }
        public string? ModelId { get; set; }
        public string? CredentialKey { get; set; }
        public Dictionary<string, string>? Options { get; set; }
        public bool SupportsImages { get; set; }
        public int ContextWindowSize { get; set; }
        public decimal? InputCostPerMillionTokens { get; set; }
        public decimal? OutputCostPerMillionTokens { get; set; }
        public string? OllamaKeepAlive { get; set; }
        public bool SupportsThinking { get; set; }
        public int VectorDimensions { get; set; }
        public ChatOptionsFile ChatOptions { get; set; } = new();
    }

    private sealed class ChatOptionsFile
    {
        public float? Temperature { get; set; }
        public float? TopP { get; set; }
        public float? FrequencyPenalty { get; set; }
        public float? PresencePenalty { get; set; }
        public int? TopK { get; set; }
        public int? MaxOutputTokens { get; set; }
        public long? Seed { get; set; }
        public ReasoningEffort? ReasoningEffort { get; set; }
        public bool? AllowMultipleToolCalls { get; set; }
    }
}

