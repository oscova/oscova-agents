using Oscova.Agents.Browsing;
using Oscova.Agents.Infrastructure.Projects;
using Oscova.Agents.Core.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Oscova.Agents.FileBackups;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Conversations;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Personas;
using Oscova.Agents.Infrastructure.Agents;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Core.Llm;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Llm;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Tasks;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Library;
using Oscova.Agents.Infrastructure.Mcp;
using Oscova.Agents.Infrastructure.Memory;
using Oscova.Agents.Infrastructure.Notifications;
using Oscova.Agents.Infrastructure.OAuth;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Infrastructure.Tasks;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Infrastructure.Tools;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Memory;
using Oscova.Agents.Notifications;
using Oscova.Agents.Plugins;
using Oscova.Agents.Policies;
using Oscova.Agents.Runtime;
using Oscova.Agents.Secrets;
using Oscova.Agents.Skills;
using Oscova.Agents.Tasks;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;
using System.Collections.Concurrent;
using Oscova.Agents.Collections;

namespace Oscova.Agents.Infrastructure.Users;

/// <summary>
/// Manages per-user service scopes for the lifetime of the process.
/// </summary>
/// <remarks>
/// Scopes are intentionally retained indefinitely because each one owns an active agent pipeline,
/// MCP server connections, and a Discord bot client that must survive page refreshes and
/// Blazor circuit reconnects. This design is suitable for a small, fixed set of users
/// (household / personal-assistant deployments). It is not appropriate for multi-tenant
/// or public-facing deployments where the user population is large or unbounded.
/// </remarks>
public sealed class UserServiceManager(
    IServiceScopeFactory serviceScopeFactory,
    IServiceProvider serviceProvider,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ILogger<UserServiceManager> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, UserServiceScope> _scopes = new(StringComparer.Ordinal);
    private readonly Lock _scopeCreationLock = new();
    private int _disposed;

    /// <summary>Checks only the task owner's foreground requests before an automatic scheduled start.</summary>
    public async Task<bool> CanScheduleTaskAsync(AgentTaskDefinition task, CancellationToken ct = default)
    {
        if (!task.WaitForOwnerProviderIdle) { return true; }

        var scope = await GetOrCreateScopeAsync(task.UserId, ct).ConfigureAwait(false);
        var activity = serviceProvider.GetRequiredService<ModelProviderActivity>();
        // Seeded tasks find their contribution by seed origin, as the executor does.
        if (task.InstructionSource is { } seedKey && scope.TaskContributions.TryGetValue(seedKey, out var registration))
        {
            var requirements = registration.Contribution.RequiredProviders?.Invoke(registration.Context, task) ?? [];
            if (requirements.Any(id => activity.IsForegroundActive(task.UserId, id))) { return false; }

            if (registration.Contribution.DeterministicHandlerFactory is not null) { return true; }
        }

        try
        {
            var agent = scope.AppService.ResolveAgentDefinition(task.AgentId);
            var surface = serviceProvider.GetRequiredService<TaskRunScopeResolver>().ResolveOrThrow(
                task.RunScope, agent, effective => scope.CrewService.ResolveAllowedDirectories(task.UserId, effective, []));
            var provider = scope.AppService.ResolveScheduledChatModelProvider(surface.EffectiveAgent, task.ModelOverrideId);
            return !activity.IsForegroundActive(task.UserId, provider.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Invalid or unavailable configuration is handled by the normal execution admission path.
            return true;
        }
    }

    /// <summary>
    /// Returns the service scope for <paramref name="userId"/>, creating and initializing it if needed.
    /// </summary>
    /// <remarks>
    /// This synchronous overload exists solely for the scoped DI factory registered in
    /// <c>Program.cs</c>. DI factories are inherently synchronous, so sync-over-async is
    /// unavoidable there. All other callers must use <see cref="GetOrCreateScopeAsync"/> instead.
    /// </remarks>
    public UserServiceScope GetOrCreateScope(string userId) =>
        GetOrCreateScopeAsync(userId).GetAwaiter().GetResult();

    /// <summary>
    /// Returns the service scope for <paramref name="userId"/> asynchronously,
    /// creating and initializing it if needed. Prefer this over
    /// <see cref="GetOrCreateScope"/> for all non-DI-factory call sites.
    /// </summary>
    public async Task<UserServiceScope> GetOrCreateScopeAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!serviceProvider.GetRequiredService<IContentProtector>().IsAvailable(userId))
        {
            throw new InvalidOperationException("Unlock your Vault Password before continuing.");
        }
        // Shared configuration is owned by the operator vault, including background-run setup.
        var operatorId = await serviceProvider.GetRequiredService<OperatorVaultAccess>()
            .RequireAsync(userId, ct).ConfigureAwait(false);
        // Initialize the owner before taking the scope creation lock or binding a consumer.
        var operatorScope = operatorId == userId ? null :
            await GetOrCreateScopeAsync(operatorId, ct).ConfigureAwait(false);
        if (!_scopes.ContainsKey(userId))
        {
            await serviceProvider.GetRequiredService<IAgentCatalog>().ReloadUserAsync(userId, ct).ConfigureAwait(false);
            await serviceProvider.GetRequiredService<IAgentTaskCatalog>().ReloadUserAsync(userId, ct).ConfigureAwait(false);
        }

        var scope = GetOrCreateRetainedScope(userId, operatorScope);
        var initialized = scope.AppService.IsInitialized;
        await scope.EnsureInitializedAsync().ConfigureAwait(false);
        await serviceProvider.GetRequiredService<SkillGovernanceService>().EnsureRecipientAsync(userId, ct).ConfigureAwait(false);
        if (!initialized) { await NotifyUnavailableProvidersAsync(scope, ct).ConfigureAwait(false); }

        return scope;
    }

    /// <summary>
    /// Returns the existing scope for <paramref name="userId"/> only if one is already cached;
    /// never constructs a new scope. Used by background services that must not trigger scope construction.
    /// </summary>
    public bool TryGetExistingScope(string userId, out UserServiceScope scope)
    {
        return _scopes.TryGetValue(userId, out scope!);
    }

    /// <summary>Gets the user ids that currently have a retained runtime scope.</summary>
    public IReadOnlyCollection<string> ActiveUserIds => [.. _scopes.Keys];

    /// <summary>Refreshes retained consumers after another user saves a shared agent.</summary>
    public async Task RefreshAgentConsumersAsync(string editorUserId, string agentId)
    {
        foreach (var userId in ActiveUserIds)
        {
            if (userId == editorUserId || !TryGetExistingScope(userId, out var scope) ||
                !scope.AppService.CustomAgentDefinitions.Any(agent => agent.Id == agentId))
            {
                continue;
            }

            // Refresh definitions before notifying open chats of the changed privacy policy.
            scope.AppService.InvalidateAllCachedAgents();
            await scope.AppService.ReloadCustomAgentsAsync().ConfigureAwait(false);
        }
    }

    private UserServiceScope GetOrCreateRetainedScope(string userId, UserServiceScope? operatorScope)
    {
        if (_scopes.TryGetValue(userId, out var existing))
        {
            return existing;
        }

        // Scope creation is rare and must be unique because each retained scope owns a worker.
        lock (_scopeCreationLock)
        {
            if (_scopes.TryGetValue(userId, out existing))
            {
                return existing;
            }

            var created = CreateScope(userId, operatorScope);
            _scopes[userId] = created;
            return created;
        }
    }

    /// <summary>
    /// Refreshes profile metadata for an already-retained scope without rebuilding the user's runtime services.
    /// </summary>
    public bool RefreshProfile(string userId, string displayName, string userName, string userEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!_scopes.TryGetValue(userId, out var scope))
        {
            return false;
        }

        scope.RefreshProfile(displayName, userName, userEmail);
        return true;
    }

    /// <summary>Refreshes authorization roles for an already-retained scope without rebuilding the user's runtime services.</summary>
    public bool RefreshRoles(string userId, IReadOnlySet<string> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!_scopes.TryGetValue(userId, out var scope))
        {
            return false;
        }

        scope.RefreshRoles(roles);
        return true;
    }

    /// <summary>
    /// Removes and disposes a cached user scope after the user is no longer valid.
    /// </summary>
    public async Task<bool> RemoveScopeAsync(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!_scopes.TryRemove(userId, out var scope))
        {
            return false;
        }

        await scope.DisposeOwnedAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Eagerly initializes a service scope for every registered user and starts enabled chat-channel bots.
    /// Called once from <c>Program.cs</c> at startup.
    /// </summary>
    /// <remarks>
    /// Eager initialization is intentional: channel bots must be running before the first
    /// user opens a browser tab, so lazy/on-demand initialization is not an option here.
    /// Because the user set is small and fixed this has no meaningful startup cost.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var dbContext = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var userIds = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id != WellKnownUsers.SystemId)
            .Select(user => user.Id)
            .ToListAsync(ct).ConfigureAwait(false);

            foreach (var userId in userIds)
            {
                try
                {
                    // Self-heal any user row that pre-dates eager provisioning or whose main agent/task was deleted.
                    await ProvisionUserAsync(userId, ct).ConfigureAwait(false);

                    await StartEnabledBotsAsync(userId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to initialize the user service scope for user '{UserId}'.", userId);
                }
            }
        }
    }

    /// <summary>Starts each enabled, stopped chat bot independently for the supplied user.</summary>
    public async Task StartEnabledBotsAsync(string userId, CancellationToken ct = default)
    {
        if (!serviceProvider.GetRequiredService<Oscova.Agents.Secrets.IContentProtector>().IsAvailable(userId)) { return; }

        await GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var channelManager = serviceProvider.GetRequiredService<IChannelManager>();

        foreach (var channel in serviceProvider.GetRequiredService<IChannelCatalog>().Channels)
        {
            try { await channelManager.StartIfEnabledAsync(userId, channel.Id, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start channel {ChannelId} for user {UserId}.", channel.Id, userId);
            }
        }

    }

    /// <summary>Notifies an unlocked account when its saved provider choices need attention.</summary>
    public async Task NotifyUnavailableProvidersAsync(UserServiceScope scope, CancellationToken ct = default)
    {
        var settings = scope.ModelProviderSettings;
        var chatIds = settings.ModelProviderConfigs.Where(provider => !settings.EmbeddingProviderIds.Contains(provider.Id))
            .Select(provider => provider.Id).ToHashSet();
        var embeddingIds = settings.ModelProviderConfigs.Where(provider => settings.EmbeddingProviderIds.Contains(provider.Id))
            .Select(provider => provider.Id).ToHashSet();
        var unavailable = (settings.SelectedChatModelProviderId is { } chat && !chatIds.Contains(chat)) ||
            (settings.FallbackChatModelProviderId is { } fallback && !chatIds.Contains(fallback)) ||
            (settings.EmbeddingModelProviderId is { } embedding && !embeddingIds.Contains(embedding));
        var tasks = await scope.AgentTaskRepository.GetTasksByUserAsync(scope.UserId, ct).ConfigureAwait(false);
        var overrides = scope.AppService.CustomAgentDefinitions.Append(scope.AppService.MainAgentDefinition)
            .Select(agent => agent.ModelOverrideId).Concat(tasks.Select(task => task.ModelOverrideId));
        unavailable |= overrides.Any(value => !string.IsNullOrWhiteSpace(value) &&
            (!Guid.TryParse(value, out var id) || !chatIds.Contains(id)));
        if (!unavailable) { return; }

        var text = serviceProvider.GetRequiredService<Oscova.Agents.Core.Localization.IServerTextLocalizer>();
        var culture = scope.UiSettings.CultureName;
        await serviceProvider.GetRequiredService<NotificationHub<NotificationMessage>>().NotifyAsync(scope.UserId,
            new NotificationMessage(
                text.Localize(culture, Core.Localization.NotificationTextKeys.ProviderAttentionTitle, "Choose an available model provider"),
                text.Localize(culture, Core.Localization.NotificationTextKeys.ProviderAttentionBody, "A saved model provider is unavailable. Choose an available provider in Settings and review the provider selections of your agents and tasks."),
                NotificationKind.Generic, UserId: scope.UserId)).ConfigureAwait(false);
    }

    private UserServiceScope CreateScope(string userId, UserServiceScope? operatorScope)
    {
        using var db = dbContextFactory.CreateDbContext();
        var profile = db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.FullName, user.UserName, user.Email })
            .FirstOrDefault();
        if (profile is null)
        {
            throw new InvalidOperationException($"Cannot create a service scope because user '{userId}' does not exist.");
        }

        var roles = db.UserRoles
            .Where(userRole => userRole.UserId == userId)
            .Join(db.Roles, userRole => userRole.RoleId, role => role.Id, (_, role) => role.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scope = serviceScopeFactory.CreateScope();
        var pluginContributions = new PluginContributionLifetime(logger);
        LibraryIndexingService? libraryIndexingService = null;
        ChatMemoryService? chatMemoryService = null;
        try
        {
            var services = scope.ServiceProvider;
            var displayName = profile?.FullName ?? string.Empty;
            var userName = profile?.UserName ?? string.Empty;
            var userEmail = profile?.Email ?? string.Empty;

            var uiSettings = ActivatorUtilities.CreateInstance<AssistantUiSettings>(services, userId);
            var profileSettings = ActivatorUtilities.CreateInstance<UserProfileSettings>(services, userId);
            var projectsSettings = ActivatorUtilities.CreateInstance<ProjectsSettings>(services, userId);
            var agentGoalSettings = ActivatorUtilities.CreateInstance<AgentGoalSettings>(services, userId);
            var projectAccess = new ProjectAccessService(projectsSettings,
                services.GetRequiredService<IUserDirectoryService>(), userId, PathUtil.AppDataPath,
                services.GetRequiredService<IUserPermissionService>(), services.GetRequiredService<NodeProjectExecutor>());
            var modelProviderSettings = ActivatorUtilities.CreateInstance<ModelProviderSettings>(services, userId);
            if (operatorScope is not null)
            {
                modelProviderSettings.BindOperatorCatalog(operatorScope.ModelProviderSettings, services.GetRequiredService<IUserPermissionService>());
            }

            var mcpToolSettings = ActivatorUtilities.CreateInstance<McpToolSettings>(services, userId);
            mcpToolSettings.BindGovernance(userId, operatorScope?.McpToolSettings,
                services.GetRequiredService<IUserPermissionService>(),
                services.GetRequiredService<Oscova.Agents.Core.Auth.ExecutionPermissionRegistry>(),
                (nodeId, token) => services.GetRequiredService<Oscova.Agents.Infrastructure.Auth.NodeCredentialStore>()
                    .IsEnrolledAsync(userId, nodeId, token));
            var toolGovernanceSettings = ActivatorUtilities.CreateInstance<ToolGovernanceSettings>(services, userId);
            var speechSettings = ActivatorUtilities.CreateInstance<SpeechSettings>(services, userId);
            var privacySettings = ActivatorUtilities.CreateInstance<PrivacySettings>(services, userId);
            var onboardingSettings = ActivatorUtilities.CreateInstance<OnboardingSettings>(services, userId);
            var channelCatalog = services.GetRequiredService<IChannelCatalog>();
            var skillScriptExecutionSettings = services.GetRequiredService<ExecutionSettings>();
            var soundSettings = ActivatorUtilities.CreateInstance<SoundSettings>(services, userId);
            var systemAvatarSettings = services.GetRequiredService<AvatarSettings>();
            var systemAuthSettings = services.GetRequiredService<AuthSettings>();
            var systemModelDownloadSettings = services.GetRequiredService<ModelDownloadSettings>();
            var systemPluginActivationSettings = services.GetRequiredService<PluginActivationSettings>();
            var systemSkillRegistrySettings = services.GetRequiredService<SkillRegistrySettings>();

            var credentialService = ActivatorUtilities.CreateInstance<CredentialService>(services, userId);
            var redactionService = ActivatorUtilities.CreateInstance<RedactionService>(services, credentialService);
            var systemCredentialService = ActivatorUtilities.CreateInstance<SystemCredentialService>(services);
            var oAuthTokenService = ActivatorUtilities.CreateInstance<OAuthTokenService>(
                services,
                credentialService,
                systemCredentialService);
            var storedThreads = ActivatorUtilities.CreateInstance<ChatThreadRepository>(
                services,
                (Func<string>)(() => PathUtil.UsersPath),
                redactionService);
            var browserWorkspace = ActivatorUtilities.CreateInstance<BrowserWorkspaceService>(services, userId, storedThreads);
            IChatThreadRepository chatThreadRepository = new BrowserChatThreadRepository(storedThreads, browserWorkspace,
                services.GetRequiredService<ConversationChoiceStore>(), services.GetRequiredService<IWebToolApprovalService>(),
                services.GetRequiredService<IChatAttachmentService>(), services.GetRequiredService<ConversationGoalStore>());
            var currentChatThreadService = ActivatorUtilities.CreateInstance<CurrentChatThreadService>(
                services,
                chatThreadRepository,
                uiSettings,
                userId);
            var agentTaskRepository = services.GetRequiredService<AgentTaskRepository>();
            var agentDefinitionStore = services.GetRequiredService<IAgentDefinitionRepository>();

            var userInstructionVariablesProvider = ActivatorUtilities.CreateInstance<UserInstructionVariablesProvider>(
                services,
                displayName,
                userName,
                userEmail,
                uiSettings,
                profileSettings);

            var embeddingGeneratorFactory = ActivatorUtilities.CreateInstance<EmbeddingGeneratorFactory>(
                services,
                modelProviderSettings,
                privacySettings,
                credentialService);
            embeddingGeneratorFactory.OperatorCredentials = operatorScope?.CredentialService;

            chatMemoryService = ActivatorUtilities.CreateInstance<ChatMemoryService>(
                services,
                userId,
                chatThreadRepository,
                embeddingGeneratorFactory,
                modelProviderSettings);
            var memoryToolset = ActivatorUtilities.CreateInstance<MemoryToolset>(
                services,
                userId,
                chatMemoryService,
                services.GetRequiredService<IAgentMemoryService>(),
                uiSettings,
                privacySettings,
                agentDefinitionStore);
            var memoryTools = AgentToolCollection.CreateToolsFromToolset(memoryToolset).ToList();
            libraryIndexingService = ActivatorUtilities.CreateInstance<LibraryIndexingService>(
                services,
                userId,
                embeddingGeneratorFactory,
                modelProviderSettings,
                privacySettings);

            var webNotifications = ActivatorUtilities.CreateInstance<WebNotificationService>(services, chatThreadRepository, agentDefinitionStore);
            IChannelNotificationService[] notificationServices =
            [
                webNotifications,
                .. channelCatalog.Channels.Where(channel => channel.SupportsNotifications).Select(channel =>
                    new PluginChannelNotificationService(channel.Id, services.GetRequiredService<IChannelManager>(),
                        webNotifications, services.GetRequiredService<ChannelConversationBindings>(), uiSettings))
            ];

            var runContextAccessor = services.GetRequiredService<RunContextAccessor>();
            var libraryTools = AgentToolCollection
                .CreateToolsFromToolset(new LibraryToolset(
                    userId,
                    libraryIndexingService,
                    privacySettings,
                    runContextAccessor,
                    services.GetRequiredService<ILogger<LibraryToolset>>()))
                .ToList();
            var taskScheduler = services.GetRequiredService<AgentTaskSchedulerService>();
            var agentTaskToolset = new AgentTaskToolset(
                userId,
                agentTaskRepository,
                agentDefinitionStore,
                uiSettings,
                taskScheduler,
                runContextAccessor,
                services.GetRequiredService<ILogger<AgentTaskToolset>>());
            Func<IReadOnlyList<AllowedDirectory>> workspaceResolver = () => runContextAccessor.Current?.AllowedDirectories ?? [];
            var workspaceFiles = new WorkspaceFileAccess(workspaceResolver);
            var agentRunScopeFactory = new AgentRunScopeFactory(
                runContextAccessor,
                privacySettings);
            var agentOrchestrator = new AgentOrchestrator(agentRunScopeFactory);
            var mcpServerHub = ActivatorUtilities.CreateInstance<McpServerHub>(services, userId, TimeSpan.FromSeconds(5));
            mcpServerHub.BindGovernance(mcpToolSettings,
                services.GetRequiredService<Oscova.Agents.Core.Auth.ExecutionPermissionRegistry>(),
                services.GetRequiredService<IUserPermissionService>(),
                services.GetRequiredService<Oscova.Agents.Core.Nodes.INodeDispatcher>(), projectAccess,
                services.GetRequiredService<ILoggerFactory>());
            var mcpRegistryToolset = new McpRegistryToolset(
                userId,
                services.GetRequiredService<IMcpRegistryService>(),
                mcpToolSettings,
                mcpServerHub,
                agentDefinitionStore,
                agentOrchestrator,
                services.GetRequiredService<RunContextAccessor>(),
                services.GetRequiredService<ILogger<McpRegistryToolset>>());

            mcpRegistryToolset.ResolveProjectAsync = projectAccess.AuthorizeAsync;
            var skillRegistryToolset = new SkillRegistryToolset(
                userId,
                services.GetRequiredService<IGitHubSkillRegistryService>(),
                services.GetRequiredService<IGitHubSkillInstallService>(),
                systemSkillRegistrySettings,
                agentDefinitionStore,
                agentOrchestrator,
                services.GetRequiredService<RunContextAccessor>(),
                services.GetRequiredService<ILogger<SkillRegistryToolset>>());

            var builtInContributions = new AgentPluginBuilder()
                .AddToolset(_ => ActivatorUtilities.CreateInstance<SpeechToolset>(services))
                .AddToolset(_ => ActivatorUtilities.CreateInstance<VisualizationToolset>(services, workspaceFiles))
                .AddToolset(_ => new WaitToolset(() => runContextAccessor.Current?.DelegatedWork,
                    services.GetRequiredService<BackgroundShellJobRegistry>().FindCompletion))
                .AddToolset(_ => new ChatHistoryToolset(
                    userId,
                    chatThreadRepository,
                    services.GetRequiredService<IContentProtector>(),
                    uiSettings,
                    services.GetRequiredService<ILogger<ChatHistoryToolset>>()));
            var contexts = services.GetRequiredService<PluginContextFactory>();
            var clock = new PluginContextFactory.UserTimeProvider(uiSettings.GetTimeZone);
            var builtInContext = contexts.CreateBuiltIn(
                new ToolCredentialResolverAdapter(credentialService, new HashSet<string>(), pluginId: null), clock);
            RegisterBuiltInTasks(builtInContributions, services, modelProviderSettings);
            var taskContributions = new Dictionary<string, (AgentTaskContribution Contribution, AgentPluginContext Context, LoadedAgentPlugin? Plugin)>(StringComparer.Ordinal);
            AddTaskContributions(builtInContributions.Tasks, builtInContext);
            var instructionDefaults = new Dictionary<string, string>(SeedContentProvider.GetPromptDefaults(), StringComparer.Ordinal);
            var personas = services.GetRequiredService<IAgentPersonaProvider>();
            foreach (var persona in AgentPersonas.Selectable)
            {
                instructionDefaults.Add($"persona:{persona}", personas.GetSystemInstruction(persona));
            }

            // Task factories reuse the same per-user credential and account context as tool factories.
            void AddTaskContributions(
                IReadOnlyList<AgentTaskContribution> contributions,
                AgentPluginContext context,
                LoadedAgentPlugin? plugin = null)
            {
                foreach (var contribution in contributions)
                {
                    var directoryName = plugin is null
                        ? contribution.Key
                        : $"plugin-{Uri.EscapeDataString(plugin.Manifest.Id.ToLowerInvariant())}.{contribution.Key}";
                    taskContributions.Add(directoryName, (contribution, context, plugin));
                }
            }

            List<ToolsetRegistration> toolsets =
            [
                new(new NotificationToolset(
                    services.GetRequiredService<RunContextAccessor>(),
                    notificationServices)),
                new(new SkillAuthoringToolset(
                    userId,
                    services.GetRequiredService<ISkillImportService>(),
                    agentDefinitionStore,
                    agentOrchestrator,
                    services.GetRequiredService<RunContextAccessor>(),
                    services.GetRequiredService<ILogger<SkillAuthoringToolset>>())),
                new(skillRegistryToolset),
                new(agentTaskToolset),
                new(mcpRegistryToolset),
                new(new DateTimeToolset(uiSettings)),
                new(new WebToolset(
                    services.GetRequiredService<IHttpClientFactory>().CreateClient("web_fetch"),
                    services.GetRequiredService<ILogger<WebToolset>>(),
                    services.GetRequiredService<IToolOutputStore>())),
                .. ProjectToolsets.Create(workspaceResolver, workspaceFiles,
                    services.GetRequiredService<ShellExecutorFactory>(),
                    services.GetRequiredService<IShellBackgroundJobProvider>(),
                    services.GetRequiredService<IToolOutputStore>()).Select(toolset => new ToolsetRegistration(toolset)),
                new(new EncryptedFileToolset(
                    userId,
                    operatorScope?.UserId ?? userId,
                    services.GetRequiredService<IContentProtector>(),
                    workspaceResolver,
                    workspaceFiles,
                    services.GetRequiredService<IToolOutputStore>())),
                new(new NotesToolset(
                    userId,
                    services.GetRequiredService<IContentProtector>(),
                    services.GetRequiredService<IUserDirectoryService>(),
                    services.GetRequiredService<RunContextAccessor>(),
                    services.GetRequiredService<IUserFileBackupService>(),
                    services.GetRequiredService<IElicitationRouter>(),
                    services.GetRequiredService<ILogger<NotesToolset>>())),
                new(new BacklogToolset(
                    userId,
                    services.GetRequiredService<IContentProtector>(),
                    services.GetRequiredService<IUserDirectoryService>(),
                    services.GetRequiredService<RunContextAccessor>(),
                    services.GetRequiredService<IUserFileBackupService>(),
                    services.GetRequiredService<IElicitationRouter>(),
                    services.GetRequiredService<ILogger<BacklogToolset>>())),

                new(new PdfToolset(workspaceResolver, workspaceFiles, services.GetRequiredService<IToolOutputStore>())),
                .. CreateToolsetRegistrations(builtInContributions.Toolsets, builtInContext),
                new(new UserQuestionToolset(services.GetRequiredService<RunContextAccessor>())),
                new(new HostShellToolset(
                    services.GetRequiredService<ShellExecutorFactory>().CreateHostExecutorAsync,
                    services.GetRequiredService<IToolOutputStore>())),
                new(new ReadLogsTool(userId, services.GetRequiredService<IUserDirectoryService>()))
            ];

            // Only the operator can select controls for the shared Avatar runtime.
            if (roles.Contains("Admin"))
            {
                toolsets.Add(new ToolsetRegistration(ActivatorUtilities.CreateInstance<AvatarToolset>(services)));
            }

            // Optional DI toolsets join the same per-user pipeline as built-ins.
            toolsets.AddRange(services.GetServices<IAgentToolset>().Select(toolset => new ToolsetRegistration(toolset)));

            // Plugin toolsets are created per user so they resolve only that user's credential slots.
            var pluginCatalog = services.GetRequiredService<IAgentPluginCatalog>();
            foreach (var plugin in pluginCatalog.Plugins)
            {
                var pluginContext = contexts.Create(plugin,
                    new ToolCredentialResolverAdapter(credentialService, plugin.DeclaredCredentialKeys, plugin.Manifest.Id),
                    clock, oAuthTokenService, userId, browserWorkspace.ResolveToolSessionAsync, browserWorkspace.ExecuteToolAsync);
                toolsets.AddRange(CreateToolsetRegistrations(plugin.Toolsets, pluginContext, plugin.Manifest.Id));
                AddTaskContributions(plugin.Tasks, pluginContext, plugin);
                foreach (var instruction in plugin.Instructions)
                {
                    instructionDefaults.Add($"{plugin.Manifest.Id.ToLowerInvariant()}:{instruction.Key}", instruction.DefaultInstruction);
                }
            }

            // File associations are data bindings, not plugin-specific execution dispatch.
            foreach (var (topicPath, source) in PromptFileNames.PluginInstructionSources)
            {
                if (instructionDefaults.TryGetValue(source, out var content)) { instructionDefaults.Add(topicPath, content); }
            }

            IEnumerable<ToolsetRegistration> CreateToolsetRegistrations(
                IEnumerable<AgentToolsetContribution> contributions,
                AgentPluginContext context,
                string? pluginId = null)
            {
                foreach (var contribution in contributions)
                {
                    IAgentToolset toolset;
                    try
                    {
                        toolset = contribution.Factory(context)
                            ?? throw new InvalidOperationException("The toolset factory returned null.");
                        if (pluginId is not null) { pluginContributions.Add(toolset); }
                    }
                    // Mandatory built-ins fail scope creation. Optional plugin failures stay isolated.
                    catch (Exception ex) when (pluginId is not null)
                    {
                        logger.LogError(ex,
                            "Plugin toolset '{ToolsetType}' from plugin '{PluginId}' failed to initialize for user '{UserId}'.",
                            contribution.ToolsetType.FullName, pluginId, userId);
                        continue;
                    }

                    yield return new ToolsetRegistration(toolset, pluginId);
                }
            }

            browserWorkspace.Bind(toolsets.Select(registration => registration.Toolset)
                .OfType<IBrowserCapability>().Distinct().ToArray());
            // Detach persistence before reverse-order contribution disposal closes the browser's pages.
            pluginContributions.Add(browserWorkspace);

            var crewService = ActivatorUtilities.CreateInstance<AgentCompositionService>(
                services,
                userInstructionVariablesProvider,
                skillScriptExecutionSettings,
                privacySettings,
                agentOrchestrator,
                toolsets,
                projectAccess,
                ActivatorUtilities.CreateInstance<ProjectToolRouter>(services, projectAccess, skillScriptExecutionSettings),
                services.GetRequiredService<FileSkillScriptRunner>().AsFrameworkRunner(),
                memoryTools,
                libraryTools);
            crewService.BrowserWorkspace = browserWorkspace;
            crewService.NotificationChannels = notificationServices.Select(service => service.Channel).ToArray();
            crewService.MemoryWriteToolNames = MemoryToolset.WriteToolNames;
            crewService.InstructionDefaults = instructionDefaults;

            var conversationIndexingService = ActivatorUtilities.CreateInstance<ConversationIndexingService>(
                services,
                chatMemoryService,
                chatThreadRepository);

            var referenceCleanupService = ActivatorUtilities.CreateInstance<ReferenceCleanupService>(
                services,
                agentTaskRepository,
                chatThreadRepository,
                chatMemoryService,
                services.GetRequiredService<ISharedAgentPersonalContextRepository>(),
                services.GetRequiredService<IUserDirectoryService>(),
                credentialService,
                services.GetRequiredService<HookSettings>(),
                services.GetRequiredService<IChannelManager>(),
                agentGoalSettings);

            crewService.ResolveRuntimeToolsAsync = mcpServerHub.GetRunToolsAsync;
            crewService.DecorateSkills = source => new GovernedSkillSource(source, userId,
                services.GetRequiredService<SkillGovernanceService>(),
                services.GetRequiredService<Oscova.Agents.Core.Auth.ExecutionPermissionRegistry>(),
                services.GetRequiredService<RunContextAccessor>());

            var appService = ActivatorUtilities.CreateInstance<AppService>(
                services,
                uiSettings,
                profileSettings,
                projectsSettings,
                modelProviderSettings,
                services.GetRequiredService<ModelDownloadSettings>(),
                mcpToolSettings,
                toolGovernanceSettings,
                speechSettings,
                privacySettings,
                onboardingSettings,
                soundSettings,
                chatThreadRepository,
                services.GetRequiredService<NotificationHub<ChatThreadChanged>>(),
                services.GetRequiredService<NotificationHub<NotificationMessage>>(),
                userId,
                currentChatThreadService,
                services.GetRequiredService<IWebElicitationService>(),
                credentialService,
                services.GetRequiredService<IModelProviderCatalog>(),
                mcpServerHub,
                agentDefinitionStore,
                services.GetRequiredService<IUserDirectoryService>(),
                crewService,
                referenceCleanupService);
            crewService.ExecutionEnvironments = services.GetRequiredService<Oscova.Agents.Execution.IExecutionEnvironmentCatalog>();
            appService.ModelProviderCredentials = operatorScope?.CredentialService ?? credentialService;
            var taskManagementService = new AgentTaskManagementService(
                userId,
                agentTaskRepository,
                taskScheduler,
                services.GetRequiredService<TaskRunScopeResolver>(),
                services.GetRequiredService<IUserDirectoryService>(),
                modelProviderSettings,
                appService,
                crewService,
                referenceCleanupService,
                services.GetRequiredService<ILogger<AgentTaskManagementService>>());
            // The registry toolset connects MCP servers before AppService exists, so bind its post-connect UI/tool refresh here.
            mcpRegistryToolset.RefreshToolCollection = () =>
            {
                appService.SyncTools();
                appService.NotifyStateChanged();
            };
            mcpRegistryToolset.ConnectProjectToolsAsync = async (context, ct) =>
                await mcpServerHub.GetRunToolsAsync(crewService.Tools, context, ct).ConfigureAwait(false);
            mcpServerHub.ToolCollectionChanged = () =>
            {
                appService.SyncTools();
                appService.InvalidateAllCachedAgents();
                appService.NotifyStateChanged();
            };
            var webElicitationService = services.GetRequiredService<IWebElicitationService>();
            var webToolApprovalService = services.GetRequiredService<IWebToolApprovalService>();
            var toolApprovalRouter = services.GetRequiredService<IToolApprovalRouter>();
            // Set after construction because the composition service already depends on the router's scope.
            if (toolApprovalRouter is ToolApprovalRouter router)
            {
                router.FindMissingSkillScriptAsync = (skill, script, ct) =>
                    crewService.FindMissingSkillScriptAsync(userId, skill, script, ct);
                // Registry installs show what the registry will install, not only what the model typed.
                router.DescribeAsync = async (call, ct) =>
                    await mcpRegistryToolset.DescribeInstallAsync(call, ct).ConfigureAwait(false)
                    ?? skillRegistryToolset.DescribeInstall(call);
            }

            var userScope = new UserServiceScope(
                userId,
                displayName,
                userName,
                userEmail,
                roles,
                scope,
                uiSettings,
                profileSettings,
                projectsSettings,
                projectAccess,
                modelProviderSettings,
                mcpToolSettings,
                toolGovernanceSettings,
                speechSettings,
                privacySettings,
                onboardingSettings,
                skillScriptExecutionSettings,
                soundSettings,
                crewService,
                mcpServerHub,
                appService,
                webElicitationService,
                webToolApprovalService,
                toolApprovalRouter,
                currentChatThreadService,
                chatThreadRepository,
                agentTaskRepository,
                taskManagementService,
                agentDefinitionStore,
                chatMemoryService,
                conversationIndexingService,
                credentialService,
                oAuthTokenService,
                referenceCleanupService,
                libraryIndexingService,
                services.GetRequiredService<IChannelManager>(),
                notificationServices,
                redactionService,
                userInstructionVariablesProvider)
            {
                TaskContributions = taskContributions,
                PluginContributions = pluginContributions,
                BrowserWorkspace = browserWorkspace,
                AgentGoals = agentGoalSettings
            };
            // Task tools share the scope's live contribution lookup with task execution and the editor.
            agentTaskToolset.BindTaskManager(taskManagementService, userScope.GetTaskInstructionDefault);
            return userScope;
        }
        catch
        {
            try
            {
                if (libraryIndexingService is not null)
                {
                    libraryIndexingService.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    cleanupException,
                    "Failed to clean up a partially constructed user scope for user '{UserId}'.",
                    userId);
            }

            try
            {
                chatMemoryService?.Dispose();
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    cleanupException,
                    "Failed to clean up a partially constructed chat-memory service for user '{UserId}'.",
                    userId);
            }

            try
            {
                Task.Run(async () => await pluginContributions.DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(cleanupException, "Failed to dispose plugin contributions for user '{UserId}'.", userId);
            }

            try
            {
                scope.Dispose();
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    cleanupException,
                    "Failed to dispose a partially constructed dependency scope for user '{UserId}'.",
                    userId);
            }

            throw;
        }
    }

    /// <summary>Declares private maintenance tasks through the shared contribution contract.</summary>
    private static void RegisterBuiltInTasks(AgentPluginBuilder builder, IServiceProvider services, ModelProviderSettings modelProviders)
    {
        builder.AddTask(
            WellKnownTasks.VectorIndexing,
            _ => new AgentTaskDefinition
            {
                Name = "Vector Indexing",
                Instruction = VectorIndexingTaskHandler.DefaultInstruction,
                WaitForOwnerProviderIdle = true,
                Schedule = new TaskSchedule { Kind = TaskScheduleKind.Interval, IntervalMinutes = 60 },
                KeepLastRuns = 24
            },
            deterministicHandlerFactory: _ => ActivatorUtilities.CreateInstance<VectorIndexingTaskHandler>(services),
            requiredProviders: (_, _) => modelProviders.SelectedEmbeddingModelProvider is { } provider ? [provider.Id] : [])
        .AddTask(
            WellKnownTasks.DailyMemory,
            _ => new AgentTaskDefinition
            {
                Name = "Daily Memory",
                Instruction = SeedContentProvider.GetInstruction($"{WellKnownTasks.DailyMemory}.md"),
                WaitForOwnerProviderIdle = true,
                Schedule = new TaskSchedule { Kind = TaskScheduleKind.Interval, IntervalMinutes = 60 },
                KeepLastRuns = 24,
                RunScope = ReservedTaskRunScopes.DailyMemory()
            },
            agentHandlerFactory: _ => ActivatorUtilities.CreateInstance<DailyMemoryTaskHandler>(services))
        .AddTask(
            WellKnownTasks.Dreaming,
            context => new AgentTaskDefinition
            {
                Name = "Dreaming",
                WaitForOwnerProviderIdle = true,
                Instruction = SeedContentProvider.GetInstruction($"{WellKnownTasks.Dreaming}.md"),
                Schedule = new TaskSchedule
                {
                    Kind = TaskScheduleKind.Cron,
                    CronExpression = "0 3 * * *",
                    TimeZoneId = context.UserTimeZone.Id
                },
                KeepLastRuns = 30,
                RunScope = ReservedTaskRunScopes.Dreaming()
            },
            agentHandlerFactory: _ => ActivatorUtilities.CreateInstance<DreamingTaskHandler>(services))
        .AddTask(
            WellKnownTasks.Reflection,
            context => new AgentTaskDefinition
            {
                Name = "Reflection",
                Instruction = SeedContentProvider.GetInstruction($"{WellKnownTasks.Reflection}.md"),
                WaitForOwnerProviderIdle = true,
                Schedule = new TaskSchedule { Kind = TaskScheduleKind.Cron, CronExpression = "0 3 * * *", TimeZoneId = context.UserTimeZone.Id },
                ToolApprovalRunMode = ToolApprovalRunMode.AutoApproveRequired,
                KeepLastRuns = 30,
                RunScope = ReservedTaskRunScopes.Reflection()
            },
            agentHandlerFactory: _ => ActivatorUtilities.CreateInstance<ReflectionTaskHandler>(services))
        .AddTask(
            WellKnownTasks.NightShift,
            context => new AgentTaskDefinition
            {
                Name = "Night Shift",
                Instruction = SeedContentProvider.GetInstruction($"{WellKnownTasks.NightShift}.md"),
                WaitForOwnerProviderIdle = true,
                Schedule = new TaskSchedule { Kind = TaskScheduleKind.Cron, CronExpression = "0 1 * * *", TimeZoneId = context.UserTimeZone.Id },
                ToolApprovalRunMode = ToolApprovalRunMode.AutoRejectRequired,
                KeepLastRuns = 30,
                RunScope = ReservedTaskRunScopes.NightShift()
            },
            agentHandlerFactory: _ => ActivatorUtilities.CreateInstance<NightShiftTaskHandler>(services))
        .AddTask(
            WellKnownTasks.MorningBrief,
            context => new AgentTaskDefinition
            {
                Name = "Morning Brief",
                Instruction = SeedContentProvider.GetInstruction($"{WellKnownTasks.MorningBrief}.md"),
                WaitForOwnerProviderIdle = true,
                Schedule = new TaskSchedule { Kind = TaskScheduleKind.Cron, CronExpression = "0 7 * * *", TimeZoneId = context.UserTimeZone.Id },
                KeepLastRuns = 30,
                RunScope = ReservedTaskRunScopes.MorningBrief()
            },
            agentHandlerFactory: _ => ActivatorUtilities.CreateInstance<MorningBriefTaskHandler>(services));
    }

    /// <summary>
    /// Idempotently provisions per-user state required before a user's <see cref="UserServiceScope"/>
    /// can serve requests: the user's directory, main agent file, initialized scope, and seeded
    /// reserved maintenance task files. Called at user creation and at startup self-heal for every non-system user.
    /// </summary>
    public async Task ProvisionUserAsync(string userId, CancellationToken ct = default)
    {
        if (string.Equals(userId, WellKnownUsers.SystemId, StringComparison.OrdinalIgnoreCase) ||
            !serviceProvider.GetRequiredService<IContentProtector>().IsAvailable(userId))
        {
            return;
        }

        // Resolve scoped services in a transient scope — UserServiceManager itself is a singleton.
        var diScope = serviceScopeFactory.CreateAsyncScope();
        await using (diScope.ConfigureAwait(false))
        {
            var userDirectoryService = diScope.ServiceProvider.GetRequiredService<IUserDirectoryService>();
            var agentDefinitionStore = diScope.ServiceProvider.GetRequiredService<IAgentDefinitionRepository>();

            await userDirectoryService.EnsureUserDirectoryExistsAsync(userId).ConfigureAwait(false);
            ProductGuideSkillSeed.Refresh(userDirectoryService.GetSkillsDirectoryPath(userId));
            await serviceProvider.GetRequiredService<IAgentCatalog>().ReloadUserAsync(userId, ct).ConfigureAwait(false);
            await agentDefinitionStore.EnsureMainAgentAsync(userId, ct).ConfigureAwait(false);
            await serviceProvider.GetRequiredService<IAgentTaskCatalog>().ReloadUserAsync(userId, ct).ConfigureAwait(false);

            await GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
            await EnsureContributedTasksAsync(userId, ct).ConfigureAwait(false);
            await RefreshContributedAgentDefaultsAsync(userId, ct).ConfigureAwait(false);

        }
    }

    /// <summary>Refreshes only retained defaults owned by this unlocked user's installed starters.</summary>
    private async Task RefreshContributedAgentDefaultsAsync(string userId, CancellationToken ct)
    {
        var userScope = _scopes[userId];
        var scope = serviceScopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAgentDefinitionRepository>();
            var templates = scope.ServiceProvider.GetRequiredService<PrebuiltAgentCatalog>().GetAll()
                .ToDictionary(template => template.Id, template => template.SystemInstruction, StringComparer.Ordinal);
            foreach (var agent in await repository.GetAllAgentsAsync(userId, ct).ConfigureAwait(false))
            {
                if (agent.OwnerUserId != userId || agent.DefaultInstruction is null || agent.InstructionSource is null ||
                    !templates.TryGetValue(agent.InstructionSource, out var currentDefault) ||
                    string.Equals(agent.DefaultInstruction, currentDefault, StringComparison.Ordinal))
                {
                    continue;
                }

                agent.DefaultInstruction = currentDefault;
                await repository.SaveInstructionSourceAsync(userId, agent, ct).ConfigureAwait(false);
                userScope.CrewService.EvictAgent(agent.Id);
                await RefreshAgentConsumersAsync(userId, agent.Id).ConfigureAwait(false);
            }

            await userScope.AppService.ReloadCustomAgentsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Seeds active task defaults after the user's main agent and scope have initialized.</summary>
    private async Task EnsureContributedTasksAsync(string userId, CancellationToken ct)
    {
        var userScope = _scopes[userId];
        var scope = serviceScopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var repository = scope.ServiceProvider.GetRequiredService<AgentTaskRepository>();
            var plugins = scope.ServiceProvider.GetRequiredService<IAgentPluginCatalog>();
            foreach (var (directoryName, registration) in userScope.TaskContributions)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var contribution = registration.Contribution;
                    var task = contribution.DefinitionFactory(registration.Context);
                    task.Id = Guid.NewGuid().ToString();
                    task.UserId = userId;
                    task.AgentId = userScope.AppService.MainAgentDefinition.Id;
                    task.CreatedAt = DateTimeOffset.UtcNow;
                    task.CreatedByAgentId = null;
                    task.InstructionSource = directoryName;
                    task.InstructionMode = InstructionSelection.Default;
                    task.Instruction = string.Empty;
                    await repository.EnsureTaskAsync(directoryName, task, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (registration.Plugin is not null)
                {
                    var plugin = registration.Plugin;
                    logger.LogError(ex, "Plugin {PluginId} task {TaskKey} could not be seeded for user {UserId}.",
                        plugin.Manifest.Id, registration.Contribution.Key, userId);
                    plugins.ReportDiagnostic(new(AgentPluginLoadDiagnosticLevel.Warning, plugin.PluginDirectory,
                        $"Task '{registration.Contribution.Key}' could not be seeded: {ex.Message}"));
                }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var scope in _scopes.Values)
        {
            await scope.DisposeOwnedAsync().ConfigureAwait(false);
        }

        _scopes.Clear();
    }
}
