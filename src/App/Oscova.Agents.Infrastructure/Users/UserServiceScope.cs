using Oscova.Agents.Core.Channels;
using Oscova.Agents.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Library;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Core.Memory;
using Oscova.Agents.Core.OAuth;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Infrastructure.Memory;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Infrastructure.Tasks;
using Oscova.Agents.Secrets;
using Oscova.Agents.Tasks;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Users;

public sealed class UserServiceScope
{
    /// <summary>Resolves a provisioned task's instruction from its active contribution.</summary>
    public string? GetTaskInstructionDefault(AgentTaskDefinition task) =>
        task.InstructionSource is not null && TaskContributions.TryGetValue(task.InstructionSource, out var registration)
            ? registration.Contribution.DefinitionFactory(registration.Context).Instruction
            : null;

    private readonly IServiceScope _serviceScope;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private int _disposed;
    private readonly IChannelManager _channelManager;

    public UserServiceScope(
        string userId,
        string displayName,
        string userName,
        string userEmail,
        IReadOnlySet<string> roles,
        IServiceScope serviceScope,
        AssistantUiSettings uiSettings,
        UserProfileSettings profileSettings,
        ProjectsSettings projectsSettings,
        ProjectAccessService projectAccess,
        ModelProviderSettings modelProviderSettings,
        McpToolSettings mcpToolSettings,
        ToolGovernanceSettings toolGovernanceSettings,
        SpeechSettings speechSettings,
        PrivacySettings privacySettings,
        OnboardingSettings onboardingSettings,
        ExecutionSettings skillScriptExecutionSettings,
        SoundSettings soundSettings,
        AgentCompositionService crewService,
        IMcpServerHub mcpServerHub,
        AppService appService,
        IWebElicitationService webElicitationService,
        IWebToolApprovalService webToolApprovalService,
        IToolApprovalRouter toolApprovalRouter,
        ICurrentChatThreadService currentChatThreadService,
        IChatThreadRepository chatThreadRepository,
        IAgentTaskRepository agentTaskRepository,
        AgentTaskManagementService taskManagementService,
        IAgentDefinitionRepository agentDefinitionStore,
        IChatMemoryService chatMemoryService,
        ConversationIndexingService conversationIndexingService,
        ICredentialService credentialService,
        IOAuthTokenService oAuthTokenService,
        ReferenceCleanupService referenceCleanupService,
        ILibraryIndexingService libraryIndexingService,
        IChannelManager channelManager,
        IReadOnlyList<IChannelNotificationService>? channelNotificationServices = null,
        IRedactionService? redactionService = null,
        UserInstructionVariablesProvider? userInstructionVariablesProvider = null)
    {
        UserId = userId;
        DisplayName = displayName;
        UserName = userName;
        UserEmail = userEmail;
        _roles = roles;
        _serviceScope = serviceScope;
        _channelManager = channelManager;
        _userInstructionVariablesProvider = userInstructionVariablesProvider;
        UiSettings = uiSettings;
        ProfileSettings = profileSettings;
        ProjectsSettings = projectsSettings;
        ProjectAccess = projectAccess;
        ModelProviderSettings = modelProviderSettings;
        McpToolSettings = mcpToolSettings;
        ToolGovernanceSettings = toolGovernanceSettings;
        SpeechSettings = speechSettings;
        PrivacySettings = privacySettings;
        OnboardingSettings = onboardingSettings;
        ExecutionSettings = skillScriptExecutionSettings;
        SoundSettings = soundSettings;
        CrewService = crewService;
        McpServerHub = mcpServerHub;
        AppService = appService;
        WebElicitationService = webElicitationService;
        WebToolApprovalService = webToolApprovalService;
        ToolApprovalRouter = toolApprovalRouter;
        CurrentChatThreadService = currentChatThreadService;
        ChatThreadRepository = chatThreadRepository;
        AgentTaskRepository = agentTaskRepository;
        TaskManagementService = taskManagementService;
        AgentDefinitionStore = agentDefinitionStore;
        ChatMemoryService = chatMemoryService;
        ConversationIndexingService = conversationIndexingService;
        CredentialService = credentialService;
        RedactionService = redactionService ?? NoopRedactionService.Instance;
        OAuthTokenService = oAuthTokenService;
        ReferenceCleanupService = referenceCleanupService;
        LibraryIndexingService = libraryIndexingService;
        ChannelNotificationServices = channelNotificationServices ?? [];
    }

    private IReadOnlySet<string> _roles;
    private readonly UserInstructionVariablesProvider? _userInstructionVariablesProvider;

    public string UserId { get; }

    /// <summary>Gets task factories bound to this user's plugin contexts.</summary>
    public IReadOnlyDictionary<string, (AgentTaskContribution Contribution, AgentPluginContext Context, LoadedAgentPlugin? Plugin)> TaskContributions { get; init; }
        = new Dictionary<string, (AgentTaskContribution, AgentPluginContext, LoadedAgentPlugin?)>(StringComparer.Ordinal);

    internal PluginContributionLifetime? PluginContributions { get; init; }

    /// <summary>Gets the host's binding to this user's contributed browser runtime.</summary>
    public BrowserWorkspaceService? BrowserWorkspace { get; init; }

    public string DisplayName { get; private set; }

    public string UserName { get; private set; }

    public string UserEmail { get; private set; }

    public AssistantUiSettings UiSettings { get; }

    public UserProfileSettings ProfileSettings { get; }

    public ProjectsSettings ProjectsSettings { get; }

    public ProjectAccessService ProjectAccess { get; }

    public ModelProviderSettings ModelProviderSettings { get; }

    public McpToolSettings McpToolSettings { get; }

    public ToolGovernanceSettings ToolGovernanceSettings { get; }

    public SpeechSettings SpeechSettings { get; }

    public PrivacySettings PrivacySettings { get; }

    public OnboardingSettings OnboardingSettings { get; }

    public ExecutionSettings ExecutionSettings { get; }

    public SoundSettings SoundSettings { get; }

    public AgentCompositionService CrewService { get; }

    public IMcpServerHub McpServerHub { get; }

    public AppService AppService { get; }

    public IWebElicitationService WebElicitationService { get; }

    public IWebToolApprovalService WebToolApprovalService { get; }

    public ConversationChoiceStore ConversationChoices => _serviceScope.ServiceProvider.GetRequiredService<ConversationChoiceStore>();

    public ConversationGoalStore ConversationGoals => _serviceScope.ServiceProvider.GetRequiredService<ConversationGoalStore>();

    public AgentGoalSettings AgentGoals { get; init; } = null!;

    public IToolApprovalRouter ToolApprovalRouter { get; }

    public ICurrentChatThreadService CurrentChatThreadService { get; }

    public IChatThreadRepository ChatThreadRepository { get; }

    public IAgentTaskRepository AgentTaskRepository { get; }

    public AgentTaskManagementService TaskManagementService { get; }

    public IAgentDefinitionRepository AgentDefinitionStore { get; }

    public IChatMemoryService ChatMemoryService { get; }

    public ConversationIndexingService ConversationIndexingService { get; }

    public ICredentialService CredentialService { get; }

    public IRedactionService RedactionService { get; }

    public IOAuthTokenService OAuthTokenService { get; }

    public ReferenceCleanupService ReferenceCleanupService { get; }

    public ILibraryIndexingService LibraryIndexingService { get; }

    public IReadOnlyList<IChannelNotificationService> ChannelNotificationServices { get; }

    /// <summary>Serializes this user's text-channel turns before current-thread resolution.</summary>
    internal SemaphoreSlim TextChannelRunGate { get; } = new(1, 1);

    public bool IsInRole(string role)
    {
        return Volatile.Read(ref _roles).Contains(role);
    }

    /// <summary>Initializes the retained scope, retrying after a failed attempt.</summary>
    public async Task EnsureInitializedAsync()
    {
        await _initializationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var wasInitialized = AppService.IsInitialized;
            await AppService.InitializeAsync().ConfigureAwait(false);
            if (!wasInitialized)
            {
                // Budget checks resolve this scope, so indexing starts only after its settings are loaded.
                await LibraryIndexingService.QueueReconciliationAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    /// <summary>
    /// Refreshes account metadata kept by the retained user runtime scope.
    /// </summary>
    public void RefreshProfile(string displayName, string userName, string userEmail)
    {
        DisplayName = displayName;
        UserName = userName;
        UserEmail = userEmail;
        _userInstructionVariablesProvider?.RefreshProfile(displayName, userName, userEmail);
    }

    /// <summary>Refreshes the authorization roles kept by the retained user runtime scope.</summary>
    public void RefreshRoles(IReadOnlySet<string> roles)
    {
        // Publish a private snapshot because retained scopes serve concurrent requests.
        Volatile.Write(ref _roles, roles.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Disposes this retained scope when its manager releases ownership.</summary>
    internal async ValueTask DisposeOwnedAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Preserve restorable tabs before plugin shutdown emits page-close events.
        BrowserWorkspace?.Dispose();

        try
        {
            await _channelManager.StopUserAsync(UserId).ConfigureAwait(false);
        }
        finally
        {
        try
        {
            // Stop the retained Library worker before disposing the services it uses.
            await LibraryIndexingService.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (ChatMemoryService is IDisposable disposableChatMemory)
                {
                    disposableChatMemory.Dispose();
                }
            }
            finally
            {
                try
                {
                    await AppService.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        if (PluginContributions is { } contributions)
                        {
                            await contributions.DisposeAsync().ConfigureAwait(false);
                        }

                        if (_serviceScope is IAsyncDisposable asyncDisposable)
                        {
                            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            _serviceScope.Dispose();
                        }
                    }
                    finally
                    {
                        _initializationGate.Dispose();
                        TextChannelRunGate.Dispose();
                    }
                }
            }
        }
        }
    }

    private sealed class NoopRedactionService : IRedactionService
    {
        public static NoopRedactionService Instance { get; } = new();

        public Task<string> RedactTextAsync(string text, string? userId = null, CancellationToken ct = default)
            => Task.FromResult(text);

        public Task<IDictionary<string, object?>> RedactArgumentsAsync(
            IDictionary<string, object?> arguments,
            string? userId = null,
            CancellationToken ct = default)
            => Task.FromResult(arguments);

        public Task<IReadOnlyList<ChatMessage>> RedactMessagesAsync(
            IReadOnlyList<ChatMessage> messages,
            string? userId = null,
            CancellationToken ct = default)
            => Task.FromResult(messages);
    }
}
