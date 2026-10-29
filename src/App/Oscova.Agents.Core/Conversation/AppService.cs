using Oscova.Agents.Conversations;
using Markdig;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.ComponentModel;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Llm;
using Oscova.Agents.Llm;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Tasks;
using Oscova.Agents.Notifications;
using Oscova.Agents.Policies;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Core.Conversation;

public class AppService(
    ILogger<AppService> logger,
    AssistantUiSettings uiSettings,
    UserProfileSettings profileSettings,
    ProjectsSettings projectsSettings,
    ModelProviderSettings llmSettings,
    ModelDownloadSettings modelDownloadSettings,
    McpToolSettings mcpSettings,
    ToolGovernanceSettings toolGovernanceSettings,
    SpeechSettings speechSettings,
    PrivacySettings privacySettings,
    OnboardingSettings onboardingSettings,
    SoundSettings soundSettings,
    IChatThreadRepository chatThreadRepository,
    NotificationHub<ChatThreadChanged> chatThreadNotifications,
    NotificationHub<NotificationMessage> notificationMessages,
    string userId,
    ICurrentChatThreadService currentChatThreadService,
    IWebElicitationService elicitationService,
    ICredentialService credentialService,
    IModelProviderCatalog modelProviderCatalog,
    IMcpServerHub mcpServerHub,
    IAgentDefinitionRepository agentDefinitionStore,
    IUserDirectoryService userDirectoryService,
    AgentCompositionService crewService,
    ReferenceCleanupService referenceCleanupService,
    RunContextAccessor runContextAccessor,
    Oscova.Agents.Core.Auth.ExecutionPermissionRegistry executions,
    ModelProviderActivity providerActivity, ILocalModelDiagnostics? localDiagnostics = null) : INotifyPropertyChanged, IAsyncDisposable
{
    /// <summary>Resolves model credentials from the owner of the approved provider catalog.</summary>
    public ICredentialService? ModelProviderCredentials { private get; set; }

    private static readonly SemaphoreSlim CustomAgentMutationGate = new(1, 1);
    private readonly ConcurrentDictionary<
        (Guid ProviderId, bool AirGapped),
        CachedModelClient> _cachedClients = [];
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private bool _clientsDisposed;

    /// <summary>Keeps native client ownership independent of replaceable routing and accounting wrappers.</summary>
    private sealed class CachedModelClient
    {
        public RawModelClient? Raw { get; set; }
        public ModelRoute? Route { get; set; }
    }

    private sealed record RawModelClient(ModelProviderConfig Config, long Version, IChatClient Client);
    private sealed record ModelRoute(ModelProviderConfig Config, long Version, bool IsDefault,
        ModelProviderConfig? Fallback, long? FallbackVersion, IChatClient Client, ModelProviderConfig EffectiveProvider);
    private IDisposable? _chatThreadChangeSubscription;

    private bool _isInitialized;

    private string _userInputText = string.Empty;

    public bool IsInitialized => _isInitialized;

    public MarkdownPipeline MarkdownPipeline { get; } = new MarkdownPipelineBuilder().Build();

    public string UserInputText
    {
        get => _userInputText;
        set
        {
            if (_userInputText != value)
            {
                _userInputText = value;
                OnPropertyChanged(nameof(UserInputText));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? StateChanged;

    public ChatThread CurrentChatThread { get; set; } = new();

    public AgentDefinition MainAgentDefinition { get; set; } = new();

    private IReadOnlyList<AgentDefinition> _customAgentDefinitions = [];

    public IReadOnlyList<AgentDefinition> CustomAgentDefinitions =>
        Volatile.Read(ref _customAgentDefinitions);

    public AgentDefinition? SelectedAgentDefinition { get; set; }

    public AgentDefinition ActiveAgentDefinition => SelectedAgentDefinition ?? MainAgentDefinition;

    public AgentDefinition ResolveAgentDefinition(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return MainAgentDefinition;
        }

        return CustomAgentDefinitions
                   .FirstOrDefault(agent => string.Equals(agent.Id, agentId, StringComparison.Ordinal))
               ?? MainAgentDefinition;
    }

    public AgentCompositionService CrewService => crewService;

    private List<ChatThread> _threads = [];

    public IReadOnlyList<ChatThread> Threads => _threads;

    public IReadOnlyList<AIFunction> AllTools => [.. crewService.Tools];

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            // Load each settings domain from its own persistence key.
            await uiSettings.LoadAsync().ConfigureAwait(false);
            await profileSettings.LoadAsync().ConfigureAwait(false);
            await projectsSettings.LoadAsync().ConfigureAwait(false);
            await llmSettings.LoadAsync().ConfigureAwait(false);
            await modelDownloadSettings.LoadAsync().ConfigureAwait(false);
            await mcpSettings.LoadAsync().ConfigureAwait(false);
            await toolGovernanceSettings.LoadAsync().ConfigureAwait(false);
            await speechSettings.LoadAsync().ConfigureAwait(false);
            await privacySettings.LoadAsync().ConfigureAwait(false);
            await onboardingSettings.LoadAsync().ConfigureAwait(false);
            await soundSettings.LoadAsync().ConfigureAwait(false);

            await EnsureChatThreadSubscriptionAsync(userId).ConfigureAwait(false);
            await LoadThreadSummariesAsync(userId).ConfigureAwait(false);
            await userDirectoryService.EnsureUserDirectoryExistsAsync(userId).ConfigureAwait(false);

            var defaultProvidersAdded = EnsureModelProviderConfigDefaults();

            if (defaultProvidersAdded)
            {
                await llmSettings.SaveAsync().ConfigureAwait(false);
            }

            crewService.InvalidateAll();
            await ReloadMcpToolsAsync().ConfigureAwait(false);
            await LoadMainAgentAsync().ConfigureAwait(false);
            await LoadCustomAgentsCoreAsync().ConfigureAwait(false);

            _isInitialized = true;
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            _isInitialized = false;
            logger.LogError(ex, "Failed to initialize AppService.");
            throw;
        }
    }

    public async Task<(bool IsRunning, string Message)> IsChatServiceRunning()
    {
        var config = llmSettings.SelectedChatModelProvider;
        if (config is null)
        {
            return (false, "No model provider is configured.");
        }

        var provider = modelProviderCatalog.Find(config.Type);
        if (provider is null)
        {
            return (false, "No model provider is configured.");
        }

        try
        {
            var apiKey = await ResolveProviderCredentialAsync(config).ConfigureAwait(false);
            var (success, message) = await provider.TestAsync(config, apiKey).ConfigureAwait(false);
            return (success, message);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Chat provider test failed for {Provider}", config.Name);
            return (false, ex.Message);
        }
    }

    public void SetSelectedChatModelProvider(Guid? providerId)
    {
        if (llmSettings.ChatProviderOverrideId == providerId)
        {
            return;
        }

        llmSettings.SelectedChatModelProviderId = providerId;
        crewService.InvalidateAllAgents();
        NotifyStateChanged();
        _ = llmSettings.TrySaveAsync();
    }

    public void NotifyModelProviderSettingsChanged()
    {
        crewService.InvalidateAllAgents();
        NotifyStateChanged();
    }

    public void SyncTools()
    {
        crewService.SyncTools(mcpServerHub.Tools, mcpServerHub.ToolMetadata);
        crewService.ApplyToolGovernance(toolGovernanceSettings);
        logger.LogDebug(
            "Crew tools synchronized. MCP tools: {McpToolCount}, total tools: {TotalToolCount}",
            mcpServerHub.Tools.Count,
            crewService.Tools.Count);
    }

    public async Task<IChatClient> GetChatClientAsync()
    {
        var config = ResolveSelectedChatModelProvider();
        return config is null
            ? throw new ModelProviderUnavailableException("No model provider configuration is selected.")
            : await GetChatClientAsync(config).ConfigureAwait(false);
    }

    public async Task<IChatClient> GetChatClientAsync(string? configId)
    {
        if (string.IsNullOrEmpty(configId))
        {
            return await GetChatClientAsync().ConfigureAwait(false);
        }

        var guid = Guid.Parse(configId);
        var config = llmSettings.ModelProviderConfigs.FirstOrDefault(c => c.Id == guid);
        return config is null
            ? throw new ModelProviderUnavailableException($"No model provider configuration found with ID {configId}.")
            : await GetChatClientAsync(config).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the effective chat model provider for an agent run.
    /// </summary>
    public ModelProviderConfig ResolveChatModelProvider(
        AgentDefinition agentDefinition,
        string? explicitModelOverrideId = null)
    {
        var requestedId = NormalizeModelProviderId(explicitModelOverrideId)
            ?? NormalizeModelProviderId(agentDefinition.ModelOverrideId);
        if (requestedId is not null)
        {
            return FindChatModelProvider(requestedId)
                ?? throw new ModelProviderUnavailableException("The selected chat provider is unavailable. Choose an approved replacement in model provider settings.");
        }

        return ResolveSelectedChatModelProvider()
            ?? throw new ModelProviderUnavailableException("The default chat provider is unavailable. Choose an approved provider in model provider settings.");
    }

    /// <summary>Resolves a scheduled task's initial route without constructing or warming a model client.</summary>
    public ModelProviderConfig ResolveScheduledChatModelProvider(AgentDefinition agent, string? modelOverrideId)
    {
        var selected = ResolveChatModelProvider(agent, modelOverrideId);
        if (_cachedClients.TryGetValue((selected.Id, privacySettings.IsLocalOnly || agent.IsLocalOnly), out var cached) &&
            cached.Route is { } route && IsCurrentRoute(route, selected) &&
            FindChatModelProvider(route.EffectiveProvider.Id.ToString()) is { } effective)
        {
            return effective;
        }

        return selected;
    }

    /// <summary>Returns configured context limits for the initial route and its approved recovery route without building clients.</summary>
    public IReadOnlyList<ModelProviderConfig> GetChatContextConfigurations(ModelProviderConfig selected, ModelProviderConfig effective)
    {
        var configurations = new List<ModelProviderConfig> { effective };
        if (IsDefaultProvider(selected) && ResolveFallbackChatModelProvider(selected) is { } fallback)
        {
            configurations.Add(fallback);
        }

        return configurations;
    }

    /// <summary>
    /// Creates or reuses a chat client for the effective provider selected by an agent run.
    /// </summary>
    public async Task<IChatClient> GetChatClientAsync(
        AgentDefinition agentDefinition,
        string? explicitModelOverrideId = null)
    {
        var resolution = await GetChatClientResolutionAsync(
            ResolveChatModelProvider(agentDefinition, explicitModelOverrideId),
            privacySettings.IsLocalOnly || agentDefinition.IsLocalOnly || runContextAccessor.Current?.AirGapped == true).ConfigureAwait(false);
        return resolution.Client;
    }

    /// <summary>
    /// Creates or reuses a chat client for an already-resolved provider while preserving the agent's local-only policy.
    /// </summary>
    public async Task<IChatClient> GetChatClientAsync(
        AgentDefinition agentDefinition,
        ModelProviderConfig modelProviderConfig)
    {
        var resolution = await GetChatClientResolutionAsync(
            modelProviderConfig,
            privacySettings.IsLocalOnly || agentDefinition.IsLocalOnly || runContextAccessor.Current?.AirGapped == true).ConfigureAwait(false);
        return resolution.Client;
    }

    /// <summary>Creates or reuses a chat client and reports the provider known to serve it.</summary>
    public async Task<(IChatClient Client, ModelProviderConfig EffectiveProvider)> GetChatClientResolutionAsync(
        AgentDefinition agentDefinition,
        ModelProviderConfig modelProviderConfig,
        bool requireLocalOnly = false)
    {
        return await GetChatClientResolutionAsync(
            modelProviderConfig,
            requireLocalOnly || privacySettings.IsLocalOnly || agentDefinition.IsLocalOnly || runContextAccessor.Current?.AirGapped == true).ConfigureAwait(false);
    }

    public async Task<IChatClient> GetChatClientAsync(ModelProviderConfig modelProviderConfig)
    {
        var resolution = await GetChatClientResolutionAsync(modelProviderConfig, privacySettings.IsLocalOnly).ConfigureAwait(false);
        return resolution.Client;
    }

    private async Task<(IChatClient Client, ModelProviderConfig EffectiveProvider)> GetChatClientResolutionAsync(
        ModelProviderConfig modelProviderConfig,
        bool airGapped)
    {
        var cacheKey = (modelProviderConfig.Id, airGapped);
        var cachedClient = _cachedClients.GetOrAdd(cacheKey, static _ => new CachedModelClient());
        if (cachedClient.Route is { } route && IsCurrentRoute(route, modelProviderConfig))
        {
            // A default-provider cache entry may be served by its fallback, so re-check the actual provider.
            var provider = ResolveModelProvider(route.EffectiveProvider);
            ThrowIfAirGappedCloudProvider(route.EffectiveProvider, provider, airGapped);
            return (route.Client, route.EffectiveProvider);
        }

        cachedClient.Route = null;
        var snapshot = modelProviderConfig.Clone();
        var version = executions.GetConfigurationVersion(ModelProviderExecution.Key(snapshot.Id));
        var isDefault = IsDefaultProvider(snapshot);
        var fallback = isDefault ? ResolveFallbackChatModelProvider(snapshot)?.Clone() : null;
        var fallbackVersion = fallback is null ? (long?)null : executions.GetConfigurationVersion(ModelProviderExecution.Key(fallback.Id));
        (IChatClient Client, ModelProviderConfig EffectiveProvider) resolution;
        if (isDefault)
        {
            resolution = await BuildDefaultClientWithResilienceAsync(snapshot, version, fallback, fallbackVersion, airGapped).ConfigureAwait(false);
        }
        else
        {
            var client = await BuildRawClientAsync(snapshot, version, airGapped).ConfigureAwait(false);
            resolution = (client, snapshot);
        }

        cachedClient.Route = new(snapshot, version, isDefault, fallback, fallbackVersion,
            resolution.Client, resolution.EffectiveProvider.Clone());

        return resolution;
    }

    private bool IsCurrentRoute(ModelRoute route, ModelProviderConfig config)
    {
        if (_clientsDisposed || !route.Config.EqualsConfiguration(config) || route.IsDefault != IsDefaultProvider(config) ||
            route.Version != executions.GetConfigurationVersion(ModelProviderExecution.Key(config.Id))) { return false; }

        var fallback = route.IsDefault ? ResolveFallbackChatModelProvider(config) : null;
        return route.Fallback is null ? fallback is null : fallback is not null &&
            route.Fallback.EqualsConfiguration(fallback) &&
            route.FallbackVersion == executions.GetConfigurationVersion(ModelProviderExecution.Key(fallback.Id));
    }

    private async Task<IChatClient> BuildRawClientAsync(
        ModelProviderConfig config,
        long version,
        bool airGapped,
        CancellationToken cancellationToken = default)
    {
        var provider = ResolveModelProvider(config);
        ThrowIfAirGappedCloudProvider(config, provider, airGapped);
        await llmSettings.RequireAccessAsync(config.Id, cancellationToken).ConfigureAwait(false);
        using var admission = ModelProviderExecution.Admit(config, version, llmSettings, executions, runContextAccessor, false, cancellationToken);
        var errors = ModelProviderValidation.Validate(config, provider);
        if (errors.Count > 0) { throw new ModelProviderUnavailableException(string.Join(" ", errors.Values)); }

        await _clientGate.WaitAsync(admission.Token).ConfigureAwait(false);
        IChatClient client;
        try
        {
            ObjectDisposedException.ThrowIf(_clientsDisposed, this);
            var entry = _cachedClients.GetOrAdd((config.Id, airGapped), static _ => new CachedModelClient());
            if (entry.Raw is { } raw && raw.Version == version && ModelProviderExecution.Equivalent(raw.Config, config))
            {
                client = raw.Client;
            }
            else
            {
                entry.Raw?.Client.Dispose();
                entry.Raw = null;
                var apiKey = await ResolveProviderCredentialAsync(config, admission.Token).ConfigureAwait(false);
                client = await provider.CreateClientAsync(config, apiKey, admission.Token).ConfigureAwait(false);
                entry.Raw = new(config.Clone(), version, client);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ModelProviderUnavailableException(
                $"Model provider '{config.Id}' could not initialize a client.",
                ex);
        }
        finally
        {
            _clientGate.Release();
        }

        // Every retained client checks the current catalog before sending another request.
        var governedClient = new ModelProviderExecution.ChatClient(
            client, config.Clone(), version, llmSettings, executions, runContextAccessor, providerActivity);
        return new UsageCaptureChatClient(
            governedClient,
            config.Id,
            config.ModelId ?? string.Empty,
            ClassifyEgress(config) == EgressClass.Cloud ? config.InputCostPerMillionTokens : 0m,
            ClassifyEgress(config) == EgressClass.Cloud ? config.OutputCostPerMillionTokens : 0m,
            runContextAccessor);
    }
    private IModelProvider ResolveModelProvider(ModelProviderConfig config)
    {
        return modelProviderCatalog.Find(config.Type)
            ?? throw new ModelProviderUnavailableException($"No model provider registered for type {config.Type}.");
    }

    private EgressClass ClassifyEgress(ModelProviderConfig config)
    {
        var provider = ResolveModelProvider(config);
        return EgressClassifier.Classify(
            config.Type,
            config.BaseUrl,
            provider.DefaultEndpoint,
            config.IsLocalEndpoint,
            provider.DefaultIsLocalEndpoint);
    }

    /// <summary>
    /// Wraps a built client so each model call records the agent's provider egress into the per-turn ledger.
    /// </summary>
    public IChatClient WrapWithEgressCapture(IChatClient client, string agentId, ModelProviderConfig config)
    {
        var descriptor = new ProviderEgressDescriptor(agentId, ClassifyEgress(config), config.Id.ToString(), config.ModelId);
        return client is ServedProviderEgressChatClient
            ? EgressCaptureChatClient.ScopeAgentOnly(client, descriptor, runContextAccessor)
            : EgressCaptureChatClient.RecordProvider(client, descriptor, runContextAccessor);
    }

    private static void ThrowIfAirGappedCloudProvider(
        ModelProviderConfig config,
        IModelProvider provider,
        bool airGapped)
    {
        if (airGapped &&
            EgressClassifier.Classify(
                config.Type,
                config.BaseUrl,
                provider.DefaultEndpoint,
                config.IsLocalEndpoint,
                provider.DefaultIsLocalEndpoint) == EgressClass.Cloud)
        {
            throw new ModelProviderUnavailableException(
                $"Offline mode is on: provider '{config.Id}' uses a non-local endpoint and is blocked. " +
                "Use a local model or point the provider at an approved local/LAN endpoint.");
        }
    }

    private async Task<(IChatClient Client, ModelProviderConfig EffectiveProvider)> BuildDefaultClientWithResilienceAsync(
        ModelProviderConfig defaultConfig,
        long version,
        ModelProviderConfig? fallbackConfig,
        long? fallbackVersion,
        bool airGapped)
    {
        Func<CancellationToken, ValueTask<IChatClient>>? fallbackFactory = fallbackConfig is null
            ? null
            : async cancellationToken =>
            {
                var fallbackClient = await BuildRawClientAsync(fallbackConfig, fallbackVersion!.Value, airGapped, cancellationToken).ConfigureAwait(false);
                // Both fallback paths must use the saved fallback defaults.
                var fallbackOptions = fallbackConfig.ChatOptions.Clone();
                return new ConfigureOptionsChatClient(fallbackClient, options =>
                {
                    // Replace only persisted provider defaults; run-scoped tools and instructions stay intact.
                    options.Temperature = fallbackOptions.Temperature;
                    options.TopP = fallbackOptions.TopP;
                    options.FrequencyPenalty = fallbackOptions.FrequencyPenalty;
                    options.PresencePenalty = fallbackOptions.PresencePenalty;
                    options.TopK = fallbackOptions.TopK;
                    // A run-scoped output cap is a caller budget, so recovery may lower it but never raise it.
                    options.MaxOutputTokens = options.MaxOutputTokens is { } runLimit && fallbackOptions.MaxOutputTokens is { } fallbackLimit
                        ? Math.Min(runLimit, fallbackLimit)
                        : options.MaxOutputTokens ?? fallbackOptions.MaxOutputTokens;
                    options.Seed = fallbackOptions.Seed;
                    options.Reasoning = fallbackOptions.Clone().Reasoning;
                    options.AllowMultipleToolCalls = fallbackOptions.AllowMultipleToolCalls;
                });
            };

        try
        {
            var primary = await BuildRawClientAsync(defaultConfig, version, airGapped).ConfigureAwait(false);
            var client = new FallbackChatClient(
                primary,
                fallbackFactory,
                defaultConfig.Id.ToString(),
                fallbackConfig is null ? null : ClassifyEgress(fallbackConfig),
                fallbackConfig?.Id.ToString(),
                fallbackConfig?.ModelId,
                runContextAccessor,
                userId,
                logger,
                notificationMessages);
            return (client, defaultConfig);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (fallbackConfig is null)
            {
                throw new ModelProviderUnavailableException(
                    $"Default model provider '{defaultConfig.Id}' could not be initialized and no fallback is configured. {ex.Message}",
                    ex);
            }

            IChatClient fallbackClient;
            try
            {
                fallbackClient = await fallbackFactory!(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                throw new ModelProviderUnavailableException(
                    $"Default model provider '{defaultConfig.Id}' and fallback '{fallbackConfig.Id}' could not be initialized.",
                    new AggregateException(ex, fallbackException));
            }

            using (logger.BeginScope(new Dictionary<string, object?> { ["userId"] = userId }))
            {
                logger.LogWarning(
                    ex,
                    "Default model provider '{ProviderId}' could not be initialized; using fallback '{FallbackProviderId}'.",
                    defaultConfig.Id,
                    fallbackConfig.Id);
            }

            var client = new ServedProviderEgressChatClient(
                fallbackClient,
                ClassifyEgress(fallbackConfig),
                fallbackConfig.Id.ToString(),
                fallbackConfig.ModelId,
                runContextAccessor);
            return (client, fallbackConfig);
        }
    }

    private bool IsDefaultProvider(ModelProviderConfig config)
    {
        return ResolveSelectedChatModelProvider()?.Id == config.Id;
    }

    private ModelProviderConfig? ResolveFallbackChatModelProvider(ModelProviderConfig defaultConfig)
    {
        var fallbackId = llmSettings.FallbackChatModelProviderId;
        if (!fallbackId.HasValue || fallbackId.Value == defaultConfig.Id)
        {
            return null;
        }

        return llmSettings.ModelProviderConfigs.FirstOrDefault(config => config.Id == fallbackId.Value);
    }

    private ModelProviderConfig? ResolveSelectedChatModelProvider()
    {
        if (llmSettings.ModelProviderConfigs.Count == 0)
        {
            return null;
        }

        return llmSettings.SelectedChatModelProvider;
    }

    private static string? NormalizeModelProviderId(string? providerId)
    {
        return string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
    }

    private ModelProviderConfig? FindChatModelProvider(string providerId)
    {
        return llmSettings.ModelProviderConfigs.FirstOrDefault(provider =>
            !llmSettings.EmbeddingProviderIds.Contains(provider.Id) && string.Equals(provider.Id.ToString(), providerId, StringComparison.OrdinalIgnoreCase));
    }

    private bool EnsureModelProviderConfigDefaults()
    {
        if (llmSettings.UsesOperatorCatalog) { return false; }

        if (!onboardingSettings.IsCompleted && IsOnlyProviderTheOldLocalOnboardingSeed())
        {
            llmSettings.ModelProviderConfigs.Clear();
            llmSettings.SelectedChatModelProviderId = null;
            llmSettings.FallbackChatModelProviderId = null;
            return true;
        }

        return false;
    }

    private bool IsOnlyProviderTheOldLocalOnboardingSeed()
    {
        if (llmSettings.ModelProviderConfigs.Count != 1)
        {
            return false;
        }

        var config = llmSettings.ModelProviderConfigs[0];
        return config.Type == ModelProviderKeys.Local &&
            llmSettings.SelectedChatModelProviderId == config.Id &&
            !string.IsNullOrWhiteSpace(modelDownloadSettings.DefaultLocalModelFileName) &&
            string.Equals(config.ModelId, modelDownloadSettings.DefaultLocalModelFileName, StringComparison.Ordinal);
    }

    public async Task ReloadMcpToolsAsync()
    {
        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await mcpServerHub.LoadToolsAsync(mcpSettings.GetAvailable(), cts.Token).ConfigureAwait(false);
            crewService.SyncTools(mcpServerHub.Tools, mcpServerHub.ToolMetadata);
            crewService.ApplyToolGovernance(toolGovernanceSettings);
            logger.LogDebug(
                "Reloaded MCP tools from settings. Configured servers: {ServerCount}, connected MCP tools: {McpToolCount}, total tools: {TotalToolCount}",
                mcpSettings.McpServerConfigs.Count,
                mcpServerHub.Tools.Count,
                crewService.Tools.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load MCP tools from configured servers.");
        }
    }

    // Loads the user's main agent. The file is seeded eagerly by UserServiceManager.ProvisionUserAsync
    // at user creation and at startup pre-warm; reaching this method with no file is a broken invariant.
    public async Task LoadMainAgentAsync()
    {
        var agent = await agentDefinitionStore.GetMainAgentAsync(userId).ConfigureAwait(false);
        if (agent is null)
        {
            logger.LogError("No main agent found for user {UserId}; user provisioning was skipped or the file was deleted.", userId);
            throw new InvalidOperationException(
                $"User '{userId}' has no main agent. ProvisionUserAsync must run before AppService.InitializeAsync.");
        }

        MainAgentDefinition = agent;
    }

    public async Task SaveMainAgentAsync(bool initializeInstructionSource = false)
    {
        if (initializeInstructionSource)
        {
            await agentDefinitionStore.SaveInstructionSourceAsync(userId, MainAgentDefinition).ConfigureAwait(false);
        }
        else { await agentDefinitionStore.SaveAgentAsync(userId, MainAgentDefinition).ConfigureAwait(false); }

        crewService.EvictAgent(MainAgentDefinition.Id);
        // Active chat components must immediately apply changes to the agent's privacy policy.
        NotifyStateChanged();
    }

    public async Task ReloadCustomAgentsAsync()
    {
        await RunCustomAgentMutationAsync(async () =>
        {
            await LoadCustomAgentsCoreAsync().ConfigureAwait(false);
            NotifyStateChanged();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes file-backed agent definitions after an out-of-band runtime action changes an agent.
    /// </summary>
    public async Task ReloadAgentDefinitionsAsync()
    {
        await RunCustomAgentMutationAsync(async () =>
        {
            await LoadMainAgentAsync().ConfigureAwait(false);
            await LoadCustomAgentsCoreAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task LoadCustomAgentsCoreAsync()
    {
        Volatile.Write(ref _customAgentDefinitions, await LoadCustomAgentsSnapshotAsync().ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<AgentDefinition>> LoadCustomAgentsSnapshotAsync()
    {
        var all = await agentDefinitionStore.GetAllAgentsAsync(userId).ConfigureAwait(false);
        return all
            .Where(agent => !agent.IsMain)
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Creates an editable custom agent from blank defaults or a prepared starter.</summary>
    public Task<AgentDefinition> CreateCustomAgentAsync(AgentDefinition? template = null, bool initializeInstructionSource = false)
    {
        return RunCustomAgentMutationAsync(async () =>
        {
            var existing = await LoadCustomAgentsSnapshotAsync().ConfigureAwait(false);
            var agent = new AgentDefinition
            {
                AllowedWorkspaces = [UserFileNames.DefaultWorkspaceDirectory]
            };
            agent.Name = AgentCallableName.MakeUniqueDisplayName(
                template?.Name ?? agent.Name, existing.Select(item => item.Name));
            if (template is not null)
            {
                agent.Description = template.Description;
                agent.SystemInstruction = template.SystemInstruction;
                if (initializeInstructionSource)
                {
                    agent.InstructionSource = template.InstructionSource;
                    agent.InstructionMode = template.InstructionMode;
                    agent.DefaultInstruction = template.DefaultInstruction;
                }

                agent.AllowedToolNames = [.. template.AllowedToolNames];
                agent.IsMemoryEnabled = template.IsMemoryEnabled;
            }

            // Persist the complete fresh copy through the ordinary ownership and cache path.
            await SaveCustomAgentCoreAsync(agent, requireExisting: false,
                initializeInstructionSource: initializeInstructionSource).ConfigureAwait(false);
            return agent;
        });
    }

    public async Task<ChatClientAgent> GetOrCreateAgent(
        string userId,
        AgentDefinition agentDefinition,
        string? explicitModelOverrideId = null,
        Func<ChatClientBuilder, ChatClientBuilder>? configureBuilder = null,
        Action<ChatClientAgentOptions>? configureOptions = null,
        IEnumerable<AIContextProvider>? extraProviders = null,
        TaskRunSurface? taskRunSurface = null,
        Func<AgentDefinition, ChatClientBuilder, ChatClientBuilder>? configureAgentBuilder = null,
        bool bypassRequestedAgentCache = false)
    {
        // Read current authorization without entering the mutation gate or replacing newer published state.
        // Nested calls validate every ancestor, including the main agent when a guest is rebuilt.
        var availableAgentDefinitions = await agentDefinitionStore.GetAllAgentsAsync(userId).ConfigureAwait(false);

        // The initiating agent's offline posture is a ceiling for every model reached through agent-as-tool fan-out.
        bool activeRunAirGapped = privacySettings.IsLocalOnly || agentDefinition.IsLocalOnly || runContextAccessor.Current?.AirGapped == true;
        var effectiveProviders = new Dictionary<string, ModelProviderConfig>(StringComparer.Ordinal);

        ModelProviderConfig ResolveSelectedFor(AgentDefinition definition)
        {
            var overrideId = string.Equals(definition.Id, agentDefinition.Id, StringComparison.Ordinal)
                ? explicitModelOverrideId
                : null;
            return ResolveChatModelProvider(definition, overrideId);
        }

        ModelProviderConfig ResolveReducerProviderFor(AgentDefinition definition)
        {
            return effectiveProviders.TryGetValue(definition.Id, out var effectiveProvider)
                ? effectiveProvider
                : ResolveSelectedFor(definition);
        }

        return await crewService.GetOrCreateAgentAsync(
            userId,
            agentDefinition,
            availableAgentDefinitions,
            async (definition, _) =>
            {
                var config = ResolveSelectedFor(definition);
                var resolution = await GetChatClientResolutionAsync(
                    config,
                    activeRunAirGapped || definition.IsLocalOnly).ConfigureAwait(false);
                effectiveProviders[definition.Id] = resolution.EffectiveProvider;
                return WrapWithEgressCapture(resolution.Client, definition.Id, resolution.EffectiveProvider);
            },
            configureBuilder,
            configureOptions,
            extraProviders,
            bypassRequestedAgentCache || !string.IsNullOrWhiteSpace(explicitModelOverrideId) || taskRunSurface?.IsScopeApplied == true,
            ResolveSelectedFor,
            taskRunSurface,
            configureAgentBuilder,
            ResolveReducerProviderFor).ConfigureAwait(false);
    }

    public async Task<ChatClientAgent> GetOrCreateAgent(
        IChatClient chatClient,
        string userId,
        AgentDefinition agentDefinition,
        Func<ChatClientBuilder, ChatClientBuilder>? configureBuilder = null,
        Action<ChatClientAgentOptions>? configureOptions = null,
        IEnumerable<AIContextProvider>? extraProviders = null,
        Func<AgentDefinition, ChatClientBuilder, ChatClientBuilder>? configureAgentBuilder = null)
    {
        var availableAgentDefinitions = await agentDefinitionStore.GetAllAgentsAsync(userId).ConfigureAwait(false);

        return await crewService.GetOrCreateAgentAsync(
            userId,
            agentDefinition,
            availableAgentDefinitions,
            (_, _) => Task.FromResult(chatClient),
            configureBuilder,
            configureOptions,
            extraProviders,
            configureAgentBuilder: configureAgentBuilder).ConfigureAwait(false);
    }

    public Task<bool> SaveCustomAgentAsync(AgentDefinition agent, bool requireExisting = true, string? creationDirectoryName = null)
    {
        return RunCustomAgentMutationAsync(() => SaveCustomAgentCoreAsync(agent, requireExisting, creationDirectoryName));
    }

    /// <summary>Clears one owned agent model override without overwriting a newer definition snapshot.</summary>
    public Task<bool> ClearCustomAgentModelOverrideAsync(
        string agentId,
        string expectedModelOverrideId)
    {
        return RunCustomAgentMutationAsync(async () =>
        {
            var current = await agentDefinitionStore.GetAgentAsync(userId, agentId).ConfigureAwait(false);
            if (current is null ||
                !string.Equals(current.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    current.ModelOverrideId,
                    expectedModelOverrideId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            current.ModelOverrideId = null;
            return await SaveCustomAgentCoreAsync(current, requireExisting: true).ConfigureAwait(false);
        });
    }

    private async Task<bool> SaveCustomAgentCoreAsync(
        AgentDefinition agent,
        bool requireExisting, string? creationDirectoryName = null, bool initializeInstructionSource = false)
    {
        if (agent.IsShared && string.IsNullOrWhiteSpace(agent.OwnerUserId))
        {
            agent.OwnerUserId = userId;
        }

        // The editor may mutate the cached instance in place, so diff sharing against the stored definition.
        var persistedAgent = await agentDefinitionStore.GetAgentAsync(userId, agent.Id).ConfigureAwait(false);
        if (requireExisting && persistedAgent is null)
        {
            return false;
        }

        var previousAllowedUserIds = persistedAgent?.AllowedUserIds?
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (initializeInstructionSource)
        {
            await agentDefinitionStore.SaveInstructionSourceAsync(userId, agent).ConfigureAwait(false);
        }
        else
        {
            await agentDefinitionStore.SaveAgentAsync(userId, agent, creationDirectoryName: creationDirectoryName).ConfigureAwait(false);
        }

        var definitions = CustomAgentDefinitions.ToList();
        var existing = definitions.FirstOrDefault(a => a.Id == agent.Id);
        if (existing is null)
        {
            definitions.Add(agent);
        }
        else if (!ReferenceEquals(existing, agent))
        {
            var index = definitions.IndexOf(existing);
            definitions[index] = agent;
        }

        definitions.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        Volatile.Write(ref _customAgentDefinitions, definitions);

        crewService.EvictAgent(agent.Id);

        // When an agent is no longer exposed as a tool, remove it from all other agents' tool lists
        if (!agent.IsExposedAsTool)
        {
            await RemoveAgentFromAllToolListsAsync(agent.Id).ConfigureAwait(false);
        }

        InvalidateReferencingAgents(agent.Id);
        NotifyStateChanged();

        var newAllowedUserIds = agent.AllowedUserIds?
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var droppedUserIds = previousAllowedUserIds
            .Except(newAllowedUserIds, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (droppedUserIds.Count > 0)
        {
            var reaped = await referenceCleanupService.ClearDroppedShareConsumersAsync(agent, droppedUserIds, CancellationToken.None).ConfigureAwait(false);
            if (reaped > 0)
            {
                logger.LogInformation(
                    "Removed shared-context files for {Count} users dropped from agent {AgentId}.",
                    reaped,
                    agent.Id);
            }
        }

        return true;
    }

    public Task DeleteCustomAgentAsync(string agentId)
    {
        return RunCustomAgentMutationAsync(() => DeleteCustomAgentCoreAsync(agentId));
    }

    /// <summary>
    /// Clears this user's persisted references to an agent without deleting the agent definition.
    /// </summary>
    public Task<ReferenceCleanupService.AgentCleanupCounts> ClearAgentReferencesAsync(AgentDefinition agent)
    {
        return RunCustomAgentMutationAsync(() => ClearAgentReferencesCoreAsync(agent));
    }

    private async Task DeleteCustomAgentCoreAsync(string agentId)
    {
        var agent = CustomAgentDefinitions.FirstOrDefault(a => a.Id == agentId);
        if (agent is null)
        {
            return;
        }

        var counts = await ClearAgentReferencesCoreAsync(agent).ConfigureAwait(false);
        await agentDefinitionStore.DeleteAgentAsync(userId, agentId).ConfigureAwait(false);

        var definitions = CustomAgentDefinitions
            .Where(candidate => !string.Equals(candidate.Id, agentId, StringComparison.Ordinal))
            .ToList();
        Volatile.Write(ref _customAgentDefinitions, definitions);

        if (SelectedAgentDefinition?.Id == agentId)
        {
            SelectedAgentDefinition = null;
        }

        crewService.EvictAgent(agentId);
        NotifyStateChanged();

        if (counts.Threads + counts.Tasks + counts.SharedContexts + counts.Memories > 0)
        {
            logger.LogInformation(
                "Cleared agent references on delete of {AgentId} for user {UserId}: {Threads} threads, {Tasks} tasks, {SharedContexts} shared contexts, {Memories} memories.",
                agentId,
                userId,
                counts.Threads,
                counts.Tasks,
                counts.SharedContexts,
                counts.Memories);
        }
    }

    /// <summary>Removes one unavailable tool agent from this user's owned definitions.</summary>
    public Task RemoveAgentToolReferencesAsync(string agentId) => RunCustomAgentMutationAsync(async () =>
    {
        await RemoveAgentFromAllToolListsAsync(agentId, restrictToOwnedAgents: true).ConfigureAwait(false);
        NotifyStateChanged();
    });

    private async Task<ReferenceCleanupService.AgentCleanupCounts> ClearAgentReferencesCoreAsync(
        AgentDefinition agent)
    {
        var counts = await referenceCleanupService.ClearAgentReferencesAsync(
            userId,
            agent,
            CancellationToken.None).ConfigureAwait(false);
        await RemoveAgentFromAllToolListsAsync(agent.Id, restrictToOwnedAgents: true).ConfigureAwait(false);
        return counts;
    }

    private static async Task RunCustomAgentMutationAsync(Func<Task> mutationAsync)
    {
        await CustomAgentMutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await mutationAsync().ConfigureAwait(false);
        }
        finally
        {
            CustomAgentMutationGate.Release();
        }
    }

    private static async Task<T> RunCustomAgentMutationAsync<T>(Func<Task<T>> mutationAsync)
    {
        await CustomAgentMutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await mutationAsync().ConfigureAwait(false);
        }
        finally
        {
            CustomAgentMutationGate.Release();
        }
    }

    public async Task<ProviderTestResult> TestProviderAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        var provider = modelProviderCatalog.Find(config.Type);
        if (provider is null)
        {
            return new() { Message = $"No model provider registered for {config.Type}.", Reason = "provider-unavailable" };
        }

        try
        {
            var errors = ModelProviderValidation.Validate(config, provider);
            if (errors.Count > 0) { return new() { Reason = "invalid-configuration", Message = string.Join(" ", errors.Values) }; }

            if (provider is ILocalModelDiagnosticProvider)
            {
                if (localDiagnostics is null) { return new() { Outcome = "skipped", Reason = "unsupported", Message = "Isolated diagnostics are unavailable.", TestKind = "inference" }; }

                await llmSettings.RequireAccessAsync(config.Id, cancellationToken).ConfigureAwait(false);
                var key = ModelProviderExecution.Key(config.Id);
                var version = executions.GetConfigurationVersion(key);
                // Owners may test unsaved drafts, while registry admission still protects revocation and retirement.
                using var admission = executions.RegisterConfiguration(key, version, userId, cancellationToken);
                return await localDiagnostics.TestAsync(config, admission.Token).ConfigureAwait(false);
            }

            var apiKey = await ResolveProviderCredentialAsync(config, cancellationToken).ConfigureAwait(false);
            var result = await provider.TestAsync(config, apiKey, cancellationToken).ConfigureAwait(false);
            return new() { Outcome = result.Success ? "passed" : "failed", Message = result.Message };
        }
        catch (OperationCanceledException) { return new() { Outcome = "cancelled", Reason = "request-cancelled", Message = "The provider test was cancelled." }; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Provider test threw for {Provider}", config.Name);
            return new() { Message = ex.Message, Reason = "provider-failed" };
        }
    }

    /// <summary>Gets the models exposed by a provider using an optional unsaved credential.</summary>
    public async Task<AvailableModelsResult> GetAvailableModelsAsync(
        ModelProviderConfig config,
        string? transientCredential = null,
        CancellationToken cancellationToken = default)
    {
        var provider = modelProviderCatalog.Find(config.Type);
        if (provider is null)
        {
            return new AvailableModelsResult
            {
                Success = false,
                Message = $"No model provider registered for {config.Type}."
            };
        }

        try
        {
            var apiKey = transientCredential is null
                ? await ResolveProviderCredentialAsync(config, cancellationToken).ConfigureAwait(false)
                : transientCredential.Trim();
            var models = await provider
                .GetAvailableModelsAsync(config, apiKey, cancellationToken)
                .ConfigureAwait(false);
            var shapedModels = models
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .GroupBy(model => model.Id, StringComparer.Ordinal)
                .Select(group => group.MaxBy(GetModelMetadataScore)!)
                .OrderBy(model => model.Id, StringComparer.Ordinal)
                .ToList();

            if (shapedModels.Count == 0)
            {
                var test = await provider.TestAsync(config, apiKey, cancellationToken).ConfigureAwait(false);
                return new AvailableModelsResult
                {
                    Success = test.Success,
                    Message = test.Success ? null : test.Message
                };
            }

            return new AvailableModelsResult
            {
                Success = true,
                Models = shapedModels
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Model discovery failed for provider type {ProviderType}.", config.Type);
            return new AvailableModelsResult { Success = false, Message = ex.Message };
        }
    }

    private static int GetModelMetadataScore(ModelDescriptor model)
    {
        return (model.ContextWindowSize.HasValue ? 1 : 0)
            + (model.SupportsImages.HasValue ? 1 : 0)
            + (model.SupportsThinking.HasValue ? 1 : 0);
    }

    /// <summary>
    /// Removes a deleted skill's name from every agent's allowed-skill list, syncing disk and the runtime cache.
    /// </summary>
    public async Task<int> RemoveSkillFromAgentsAsync(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return 0;
        }

        var touchedAgentCount = 0;

        // Personal uninstall must never rewrite another owner's shared agent.
        foreach (var agent in CustomAgentDefinitions.Where(agent =>
            string.Equals(agent.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)).Prepend(MainAgentDefinition))
        {
            var previousSkills = agent.AllowedSkillNames;
            var remainingSkills = previousSkills.ToList();
            if (RemoveMatching(remainingSkills, skillName) == 0) { continue; }

            agent.AllowedSkillNames = remainingSkills;
            try
            {
                await agentDefinitionStore.SaveAgentAsync(userId, agent).ConfigureAwait(false);
            }
            catch
            {
                // Retain failed assignments so a later uninstall retries their persistence.
                agent.AllowedSkillNames = previousSkills;
                throw;
            }

            crewService.EvictAgent(agent.Id);
            touchedAgentCount++;
        }

        return touchedAgentCount;
    }

    private static int RemoveMatching(IList<string> values, string value)
    {
        var removed = 0;
        for (var index = values.Count - 1; index >= 0; index--)
        {
            if (string.Equals(values[index], value, StringComparison.OrdinalIgnoreCase))
            {
                values.RemoveAt(index);
                removed++;
            }
        }

        return removed;
    }

    private async Task RemoveAgentFromAllToolListsAsync(string agentId, bool restrictToOwnedAgents = false)
    {
        if (MainAgentDefinition.AgentToolIds.Remove(agentId))
        {
            await agentDefinitionStore.SaveAgentAsync(userId, MainAgentDefinition).ConfigureAwait(false);
            crewService.EvictAgent(MainAgentDefinition.Id);
        }

        foreach (var agent in CustomAgentDefinitions)
        {
            if ((!restrictToOwnedAgents ||
                 string.Equals(agent.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)) &&
                agent.AgentToolIds.Remove(agentId))
            {
                await agentDefinitionStore.SaveAgentAsync(userId, agent).ConfigureAwait(false);
                crewService.EvictAgent(agent.Id);
            }
        }
    }

    private void InvalidateReferencingAgents(string agentId)
    {
        if (MainAgentDefinition.AgentToolIds.Contains(agentId))
        {
            crewService.EvictAgent(MainAgentDefinition.Id);
        }

        foreach (var def in CustomAgentDefinitions)
        {
            if (def.AgentToolIds.Contains(agentId))
            {
                crewService.EvictAgent(def.Id);
            }
        }
    }

    // Rebuilds cached agents on next use while keeping the current tool collection intact.
    public void InvalidateAllCachedAgents()
    {
        crewService.InvalidateAllAgents();
        logger.LogDebug("Invalidated all cached agents while preserving the current tool collection.");
    }

    public void NotifyStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartNewConversationAsync()
    {
        var agent = ActiveAgentDefinition;
        await currentChatThreadService.StartNewConversationAsync(agent.Id, CurrentChatThread.ProjectId).ConfigureAwait(false);
        CurrentChatThread = new ChatThread
        {
            AgentId = agent.Id,
            AgentName = agent.Name
        };
        UserInputText = string.Empty;
        NotifyStateChanged();
    }

    public async Task LoadConversationAsync(string threadId)
    {
        elicitationService.CancelAllPendingElicitations();
        var thread = await chatThreadRepository.GetThreadAsync(userId, threadId).ConfigureAwait(false);
        if (thread is not null)
        {
            CurrentChatThread = thread;

            if (thread.IsUnread)
            {
                thread.IsUnread = false;
                await chatThreadRepository.SaveThreadAsync(userId, thread).ConfigureAwait(false);

                var listItem = _threads.FirstOrDefault(t => t.Id == threadId);
                listItem?.IsUnread = false;
            }
        }

        UserInputText = string.Empty;
        NotifyStateChanged();
    }

    public async Task DeleteConversationAsync(string threadId)
    {
        await chatThreadRepository.DeleteThreadAsync(userId, threadId).ConfigureAwait(false);

        if (CurrentChatThread.Id == threadId)
        {
            await StartNewConversationAsync().ConfigureAwait(false);
        }

        _threads.RemoveAll(t => t.Id == threadId);
        NotifyStateChanged();
    }

    public async Task SaveCurrentThreadAsync()
    {
        await chatThreadRepository.SaveThreadAsync(userId, CurrentChatThread).ConfigureAwait(false);

        var existing = _threads.FirstOrDefault(t => t.Id == CurrentChatThread.Id);
        if (existing is not null)
        {
            existing.Title = CurrentChatThread.Title;
            existing.AgentName = CurrentChatThread.AgentName;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.IsUnread = CurrentChatThread.IsUnread;
        }
        else
        {
            _threads.Insert(0, new ChatThread
            {
                Id = CurrentChatThread.Id,
                Title = CurrentChatThread.Title,
                AgentId = CurrentChatThread.AgentId,
                AgentName = CurrentChatThread.AgentName,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsUnread = CurrentChatThread.IsUnread
            });
        }

        NotifyStateChanged();
    }

    public async Task DeleteAllConversationsAsync()
    {
        await chatThreadRepository.DeleteAllThreadsAsync(userId).ConfigureAwait(false);
        _threads.Clear();
        await StartNewConversationAsync().ConfigureAwait(false);
    }

    /// <summary>Reloads thread summaries after the user's confidential-data key is unlocked.</summary>
    /// <summary>Reloads thread summaries after the user's vault is unlocked.</summary>
    public async Task RefreshThreadSummariesAsync()
    {
        _threads = [.. await chatThreadRepository.GetThreadsAsync(userId).ConfigureAwait(false)];
        NotifyStateChanged();
    }

    private async Task LoadThreadSummariesAsync(string userId)
    {
        _threads = [.. await chatThreadRepository
            .GetThreadsAsync(userId)
            .ConfigureAwait(false)];
    }

    private Task EnsureChatThreadSubscriptionAsync(string userId)
    {
        if (_chatThreadChangeSubscription is not null)
        {
            return Task.CompletedTask;
        }

        _chatThreadChangeSubscription = chatThreadNotifications.Subscribe(userId, async _ =>
        {
            await LoadThreadSummariesAsync(userId).ConfigureAwait(false);
            NotifyStateChanged();
        });

        return Task.CompletedTask;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private async Task<string?> ResolveProviderCredentialAsync(
        ModelProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        await llmSettings.RequireAccessAsync(config.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(config.CredentialKey))
        {
            return null;
        }

        // Shared providers resolve through the operator vault, personal providers through this account.
        var ownerCredentials = llmSettings.OwnerOf(config.Id) == llmSettings ? credentialService : ModelProviderCredentials;
        var apiKey = ownerCredentials is null ? null : await ownerCredentials.ResolveFieldAsync(config.CredentialKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ModelProviderUnavailableException(
                $"Credential '{config.CredentialKey}' is not available for provider '{config.Id}'.");
        }

        return apiKey;
    }

    public async ValueTask DisposeAsync()
    {
        await _clientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _clientsDisposed = true;
            foreach (var entry in _cachedClients.Values) { entry.Raw?.Client.Dispose(); }

            _cachedClients.Clear();
        }
        finally
        {
            _clientGate.Release();
        }

        _chatThreadChangeSubscription?.Dispose();
        _chatThreadChangeSubscription = null;

        if (mcpServerHub != null)
        {
            await mcpServerHub.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
    /// <summary>Reloads user settings after a file-editor save and refreshes cached agents.</summary>
    public async Task ReloadUserSettingsAsync()
    {
        await uiSettings.ReloadAsync().ConfigureAwait(false);
        await profileSettings.ReloadAsync().ConfigureAwait(false);
        await projectsSettings.ReloadAsync().ConfigureAwait(false);
        await llmSettings.ReloadAsync().ConfigureAwait(false);
        await modelDownloadSettings.ReloadAsync().ConfigureAwait(false);
        await mcpSettings.ReloadAsync().ConfigureAwait(false);
        await toolGovernanceSettings.ReloadAsync().ConfigureAwait(false);
        await speechSettings.ReloadAsync().ConfigureAwait(false);
        await privacySettings.ReloadAsync().ConfigureAwait(false);
        await onboardingSettings.ReloadAsync().ConfigureAwait(false);
        await soundSettings.ReloadAsync().ConfigureAwait(false);

        InvalidateAllCachedAgents();
    }
}
