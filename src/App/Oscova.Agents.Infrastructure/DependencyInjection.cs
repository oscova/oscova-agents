using Oscova.Agents.Core.Channels;
using Oscova.Agents.Conversations;
using Oscova.Agents.FileBackups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Oscova.Agents.Infrastructure.Auth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using Oscova.Agents.Avatar;
using System.Net;
using System.Net.Sockets;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Infrastructure.Projects;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Llm;
using Oscova.Agents.Core.Avatar;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Core.Tasks;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Llm;
using Oscova.Agents.Secrets;
using Oscova.Agents.Speech;
using Oscova.Agents.Skills;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Tasks;
using Oscova.Agents.Infrastructure.Avatar;
using Oscova.Agents.Infrastructure.Notifications;
using Oscova.Agents.Infrastructure.OAuth;
using Oscova.Agents.Infrastructure.Agents;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Infrastructure.Skills.Registry;
using Oscova.Agents.Infrastructure.Metrics;
using Oscova.Agents.Infrastructure.Net;
using Oscova.Agents.Infrastructure.Speech;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Infrastructure.Downloads;
using Oscova.Agents.Infrastructure.Mcp.Registry;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Infrastructure.Plugins.Registry;
using Oscova.Agents.Infrastructure.Audit;
using Oscova.Agents.Infrastructure.Privacy;
using Oscova.Agents.Infrastructure.Backup;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Infrastructure.Hooks;
using Oscova.Agents.Infrastructure.Hardware;
using Oscova.Agents.Infrastructure.ToolGovernance;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.ToolGovernance;
using Oscova.Agents.Persistence;
using Oscova.Agents.Plugins;
using Oscova.Agents.Runtime;
using Oscova.Agents.Settings;
using Oscova.Agents.Tasks;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Notifications;
using Oscova.Agents.Memory;
using Oscova.Agents.Hooks;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure;

public static class DependencyInjection
{
    private static readonly Type[] BuiltInCredentialToolsetTypes = [];

    /// <summary>Registers the shared Avatar connection and persistence owners without the web host.</summary>
    public static IServiceCollection AddAvatarBridgeServices(this IServiceCollection services)
    {
        services.AddSingleton<AvatarSettings>();
        services.AddSingleton(sp => new AvatarBridgeCredentialStore(
            sp.GetRequiredService<IAppSettingsRepository>(),
            Path.Combine(PathUtil.AppDataPath, "avatar-bridge.key")));
        services.AddHttpClient(AvatarBridgeService.EndpointClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<AvatarBridgeService>();
        services.AddSingleton<IAvatarBridgeService>(sp => sp.GetRequiredService<AvatarBridgeService>());
        services.AddSingleton(sp => new AvatarRuntimeSettingsService(
            sp.GetRequiredService<AvatarSettings>(), sp.GetRequiredService<IAvatarBridgeService>(),
            sp.GetRequiredService<ILogger<AvatarRuntimeSettingsService>>(),
            Path.Combine(PathUtil.SettingsPath, "avatar.yaml.lock")));
        services.AddSingleton<IAvatarRuntimeSettingsService>(sp => sp.GetRequiredService<AvatarRuntimeSettingsService>());
        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<ProjectGitService>();
        serviceCollection.AddTransient<AirGapHttpHandler>();
        serviceCollection.AddHttpClient();
        serviceCollection.AddHttpClient(GitHubSkillApiCache.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        serviceCollection.ConfigureHttpClientDefaults(builder => builder.AddHttpMessageHandler<AirGapHttpHandler>());
        serviceCollection.AddSingleton<PluginContextFactory>();
        serviceCollection.AddHttpClient(PluginHostServices.GuardedClient)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None
            });
        serviceCollection.AddHttpClient(PluginHostServices.ChannelTransport)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
            {
                var airGap = handlers.FirstOrDefault(handler => handler is AirGapHttpHandler);
                if (airGap is not null) { handlers.Remove(airGap); }
            });
        serviceCollection.AddHttpClient("nuget-search", client =>
        {
            client.BaseAddress = new Uri("https://azuresearch-usnc.nuget.org/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Oscova-Agents/1.0");
        });
        serviceCollection.AddHttpClient("mcp-registry", client =>
        {
            client.BaseAddress = new Uri("https://registry.modelcontextprotocol.io/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Oscova-Agents/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        serviceCollection.AddHttpClient("web_fetch", client => client.Timeout = TimeSpan.FromSeconds(30))
        .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Pooled fetch clients must not carry one user's website cookies into another user's request.
            UseCookies = false,
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                var settings = sp.GetRequiredService<NetworkSettings>();
                await settings.LoadAsync().ConfigureAwait(false);
                var allowPrivate = settings.AllowPrivateNetworkFetch;

                // AirGapHttpHandler still wraps this client through ConfigureHttpClientDefaults.
                // Resolve once, validate the answer set, then connect to the same IP to prevent DNS rebinding.
                // TLS still uses the original hostname, so certificate validation is unaffected.
                var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                if (addresses.Length == 0)
                {
                    throw new HttpRequestException($"Could not resolve host '{host}'.");
                }

                // The opt-in allows private/loopback targets, but reserved and non-routable ranges stay blocked.
                if (addresses.Any(address => !SsrfAddressValidator.IsAllowedForWebFetch(address, allowPrivate)))
                {
                    throw new SsrfBlockedException(host);
                }

                Exception? lastError = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        lastError = ex;
                    }
                }

                throw new HttpRequestException($"Could not connect to '{host}'.", lastError);
            }
        });
        serviceCollection.AddAvatarBridgeServices();
        serviceCollection.AddSingleton<AvatarSpeechService>();
        serviceCollection.AddSingleton<SpeechClipSynthesizer>();
        serviceCollection.AddSingleton<ChatVisualizationService>();
        serviceCollection.AddSingleton<NetworkSettings>();
        serviceCollection.AddSingleton<ExecutionSettings>();
        serviceCollection.AddSingleton<ExecutionSettingsService>();
        serviceCollection.AddSingleton<SkillRegistrySettings>();

        serviceCollection.AddSingleton<ModelDownloadSettings>();
        serviceCollection.AddSingleton<HostHardwareProbe>();
        serviceCollection.AddSingleton<DiagnosticProcessSupervisor>();
        serviceCollection.AddSingleton<ILocalModelDiagnostics, LocalModelDiagnostics>();
        serviceCollection.AddSingleton<Oscova.Agents.Core.Hardware.IHostPowerService, HostPowerService>();
        serviceCollection.AddSingleton<AuthSettings>();
        serviceCollection.AddSingleton<HookSettings>();
        serviceCollection.AddSingleton<HostOwnershipSettings>();
        serviceCollection.AddSingleton<UpdateNotificationSettings>();
        serviceCollection.AddSingleton<PluginActivationSettings>();
        serviceCollection.AddSingleton<PluginStartupState>();

        serviceCollection.AddScoped<IAgentMemoryService>(sp =>
        {
            var userDirectories = sp.GetRequiredService<IUserDirectoryService>();
            return new AgentMemoryService(
                userDirectories.GetUserDirectoryPath,
                UserFileNames.MemoryDirectory,
                UserFileLimits.MaxEditSizeBytes,
                async userId =>
                {
                    var uiSettings = new AssistantUiSettings(
                        sp.GetRequiredService<IAppSettingsRepository>(),
                        userId,
                        sp.GetRequiredService<ILoggerFactory>());
                    await uiSettings.LoadAsync().ConfigureAwait(false);
                    var userNow = DateTime.UtcNow.ToUserTime(uiSettings.GetTimeZone());
                    return DateOnly.FromDateTime(userNow);
                },
                sp.GetRequiredService<IUserFileBackupService>(),
                sp.GetRequiredService<IContentProtector>());
        });
        serviceCollection.AddSingleton<IAppSettingsRepository, Oscova.Agents.Infrastructure.Settings.AppSettingsRepository>();
        serviceCollection.AddSingleton<Oscova.Agents.Infrastructure.Users.OperatorVaultAccess>();
        serviceCollection.AddSingleton<PendingDuressSelections>();
        serviceCollection.AddSingleton<OAuthStateStore>();
        serviceCollection.AddSingleton<OAuthProviderCatalog>();
        serviceCollection.AddSingleton<NotificationHub<ChatThreadChanged>>();
        serviceCollection.AddSingleton<NotificationHub<TaskStatusUpdate>>();
        serviceCollection.AddSingleton<NotificationHub<ChannelStatus>>();
        serviceCollection.AddSingleton<NotificationHub<NotificationMessage>>();
        serviceCollection.AddSingleton<INotificationInbox, NotificationInbox>();
        serviceCollection.AddHostedService<NotificationInboxBootstrapper>();
        serviceCollection.AddSingleton<FileSessionIndexCoordinator>();
        serviceCollection.AddScoped<IChatThreadRepository>(sp =>
            new ChatThreadRepository(
                () => PathUtil.UsersPath,
                sp.GetRequiredService<FileSessionIndexCoordinator>(),
                sp.GetRequiredService<IRedactionService>(),
                sp.GetRequiredService<IContentProtector>(),
                sp.GetRequiredService<ILogger<ChatThreadRepository>>()));
        serviceCollection.AddSingleton<IMetricsEventStore, MetricsEventStore>();
        serviceCollection.AddSingleton<ModelBudgetService>();
        serviceCollection.AddSingleton<IEgressAuditStore, EgressAuditStore>();
        serviceCollection.AddScoped<IDashboardMetricsService, DashboardMetricsService>();
        serviceCollection.AddSingleton<IChatAttachmentService, ChatAttachmentService>();
        serviceCollection.AddSingleton<ChatAttachmentManifestBuilder>();
        serviceCollection.AddSingleton<ChatAttachmentManifestEstimator>();
        serviceCollection.AddSingleton<YamlConverter>();
        serviceCollection.AddSingleton<PrebuiltAgentCatalog>();
        serviceCollection.AddSingleton<IAgentCatalog, AgentCatalog>();
        serviceCollection.AddSingleton<AgentDefinitionMutationCoordinator>();
        serviceCollection.AddSingleton<IAgentPersonaProvider, AgentPersonaProvider>();
        serviceCollection.AddScoped<IAgentOrchestrator, AgentOrchestrator>();
        serviceCollection.AddScoped<AgentDefinitionRepository>();
        serviceCollection.AddScoped<IAgentDefinitionRepository>(sp => sp.GetRequiredService<AgentDefinitionRepository>());
        serviceCollection.AddScoped<AgentPackageService>();
        serviceCollection.AddScoped<ISharedAgentPersonalContextRepository>(sp =>
        {
            var userDirectories = sp.GetRequiredService<IUserDirectoryService>();
            return new SharedAgentPersonalContextRepository(
                userId => ResolveSharedContextRoot(userDirectories, userId),
                UserFileLimits.MaxEditSizeBytes,
                UserFileLimits.MaxEncryptedEditSizeBytes,
                sp.GetRequiredService<YamlConverter>(),
                sp.GetRequiredService<IContentProtector>());
        });
        serviceCollection.AddSingleton<VaultDataProtector>();
        serviceCollection.AddSingleton<DuressRecordProtector>();
        serviceCollection.TryAddSingleton<TimeProvider>(TimeProvider.System);
        serviceCollection.AddSingleton<SignInThrottle>();
        serviceCollection.AddSingleton<IVaultDataProtector>(sp =>
            sp.GetRequiredService<VaultDataProtector>());
        serviceCollection.AddSingleton<IContentProtector>(sp =>
            sp.GetRequiredService<VaultDataProtector>());
        serviceCollection.AddScoped<ISystemCredentialService, SystemCredentialService>();
        serviceCollection.AddScoped<IRedactionService>(sp => new RedactionService(
            new EmptyCredentialService(),
            sp.GetRequiredService<ICredentialTypeRegistry>(),
            userId => ActivatorUtilities.CreateInstance<CredentialService>(sp, userId)));
        serviceCollection.AddSingleton<IAgentPluginCatalog>(sp =>
        {
            // Plugin loading is startup-only and fail-closed: discovery alone never permits code execution.
            var activationSettings = sp.GetRequiredService<PluginActivationSettings>();
            PluginFrameworkCompatibility.AdmitAsync(PathUtil.PluginsPath, activationSettings, CancellationToken.None)
                .GetAwaiter().GetResult();
            var enabledPluginIds = activationSettings.EnabledPluginIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            sp.GetRequiredService<PluginStartupState>().CaptureEnabledPluginIds(enabledPluginIds);
            var catalog = AgentPluginLoader.LoadDirectory(PathUtil.PluginsPath, enabledPluginIds);
            var logger = sp.GetRequiredService<ILogger<AgentPluginLoader>>();

            foreach (var diagnostic in catalog.Diagnostics)
            {
                if (diagnostic.Level == AgentPluginLoadDiagnosticLevel.Error)
                {
                    logger.LogError("Plugin load error at '{Path}': {Message}", diagnostic.Path, diagnostic.Message);
                    continue;
                }

                logger.LogWarning("Plugin load warning at '{Path}': {Message}", diagnostic.Path, diagnostic.Message);
            }

            return catalog;
        });
        serviceCollection.AddSingleton<ICredentialTypeRegistry>(sp =>
        {
            var pluginCatalog = sp.GetRequiredService<IAgentPluginCatalog>();
            var logger = sp.GetRequiredService<ILogger<CredentialTypeRegistry>>();
            return BuildCredentialTypeRegistry(pluginCatalog, sp.GetRequiredService<OAuthProviderCatalog>(), logger);
        });
        serviceCollection.AddSingleton<PluginProviderActivation>();
        serviceCollection.AddSingleton<RunContextAccessor>();
        serviceCollection.AddSingleton<ModelProviderActivity>();
        serviceCollection.AddSingleton<IToolOutputStore, ToolOutputStore>();
        serviceCollection.AddScoped<IWebElicitationService, WebElicitationService>();
        serviceCollection.AddScoped<IWebToolApprovalService, WebToolApprovalService>();
        serviceCollection.AddScoped<ConversationChoiceStore>();
        serviceCollection.AddScoped<ConversationGoalStore>();
        serviceCollection.AddScoped<IElicitationRouter, ElicitationRouter>();
        // Scheduler admission resolves this stateless service from the root provider.
        serviceCollection.AddSingleton<TaskRunScopeResolver>();

        // Questions and approvals share the plugin reply transport.
        serviceCollection.AddSingleton<PluginTextElicitationTransport>();
        serviceCollection.AddSingleton<ITextChannelElicitationService, TextChannelElicitationService>();
        serviceCollection.AddSingleton<ITextChannelToolApprovalService, TextChannelToolApprovalService>();
        serviceCollection.AddScoped<IToolApprovalRouter, ToolApprovalRouter>();
        serviceCollection.AddScoped<ToolApprovalRunLoop>();

        serviceCollection.AddSingleton<IDownloadService, DownloadService>();
        serviceCollection.AddSingleton(sp =>
        {
            var webRootPath = sp.GetRequiredService<IWebHostInfoProvider>().WebRootPath;
            return new SpeechClipStore(
                string.IsNullOrWhiteSpace(webRootPath) ? AppContext.BaseDirectory : webRootPath,
                sp.GetRequiredService<ILogger<SpeechClipStore>>());
        });
        serviceCollection.AddSingleton<IUserDirectoryService, UserDirectoryService>();
        serviceCollection.AddSingleton<IUserFileBackupService>(sp =>
        {
            var userDirectories = sp.GetRequiredService<IUserDirectoryService>();
            return new UserFileBackupService(
                userDirectories.GetUserDirectoryPath,
                UserEditableFileTypes.ContainsFileName,
                UserFileLimits.MaxEncryptedEditSizeBytes,
                sp.GetRequiredService<ILogger<UserFileBackupService>>());
        });
        serviceCollection.AddSingleton<IProfilePictureService, ProfilePictureService>();
        serviceCollection.AddSingleton<IWallpaperService, WallpaperService>();
        serviceCollection.AddSingleton<ExecutionEnvironmentCatalog>();
        serviceCollection.AddSingleton<Oscova.Agents.Execution.IExecutionEnvironmentCatalog>(sp => sp.GetRequiredService<ExecutionEnvironmentCatalog>());
        serviceCollection.AddSingleton<ShellExecutorFactory>();
        serviceCollection.AddSingleton<IShellExecutorProvider>(sp => sp.GetRequiredService<ShellExecutorFactory>());
        serviceCollection.AddSingleton<BackgroundShellJobRegistry>();
        serviceCollection.AddSingleton<IShellBackgroundJobProvider>(sp => sp.GetRequiredService<BackgroundShellJobRegistry>());
        serviceCollection.AddHostedService<ShellExecutorReaper>();
        serviceCollection.AddSingleton<FileSkillScriptRunner>();
        serviceCollection.AddSingleton<UserServiceManager>();
        serviceCollection.AddSingleton<RecordFormatMigration>();

        // Providers are clean singletons - no secret service dependencies
        serviceCollection.AddSingleton<IModelProvider, OllamaModelProvider>();
        serviceCollection.AddSingleton<IModelProvider, GoogleModelProvider>();
        serviceCollection.AddSingleton<IModelProvider, AnthropicModelProvider>();
        serviceCollection.AddSingleton<IModelProvider, OpenAiModelProvider>();
        serviceCollection.AddSingleton<IModelProvider, GroqModelProvider>();
        serviceCollection.AddSingleton<IModelProvider, DeepSeekModelProvider>();
        serviceCollection.AddSingleton<IModelProvider, OpenAiCompatibleModelProvider>();
        serviceCollection.AddSingleton<IModelProvider, OpenRouterModelProvider>();
        serviceCollection.AddSingleton<IModelProvider, NvidiaNimModelProvider>();
        serviceCollection.AddSingleton<IModelProviderCatalog>(sp => new ModelProviderCatalog(
            sp.GetServices<IModelProvider>(),
            sp.GetRequiredService<PluginProviderActivation>()));

        serviceCollection.AddSingleton<SpeechProviderOwnership>();
        serviceCollection.AddSingleton<ModelProviderOwnership>();
        serviceCollection.AddSingleton<ISpeechProviderCatalog>(sp => new SpeechProviderCatalog(
            sp.GetServices<ISpeechRecognitionProvider>(),
            sp.GetServices<ISpeechSynthesisProvider>(),
            sp.GetRequiredService<PluginProviderActivation>(),
            sp.GetRequiredService<ModelProviderActivity>(), sp.GetRequiredService<RunContextAccessor>()));
        serviceCollection.AddSingleton<SpeechSynthesisRouter>();
        serviceCollection.AddSingleton<SpeechRecognitionRouter>();

        serviceCollection.AddSingleton<ThreadRunGuard>();
        serviceCollection.AddSingleton<ChatSteeringRegistry>();
        serviceCollection.AddSingleton<TextChannelTurnProcessor>();
        serviceCollection.AddSingleton<ChannelConversationBindings>();
        serviceCollection.AddSingleton<ChannelCatalog>();
        serviceCollection.AddSingleton<IChannelCatalog>(sp => sp.GetRequiredService<ChannelCatalog>());
        serviceCollection.AddSingleton<IChannelManager, ChannelManager>();

        serviceCollection.AddSingleton<IAgentTaskCatalog>(sp =>
            new AgentTaskCatalog(
                () => PathUtil.UsersPath,
                sp.GetRequiredService<YamlConverter>(),
                sp.GetRequiredService<ILogger<AgentTaskCatalog>>(),
                sp.GetRequiredService<IContentProtector>(),
                static userId => Guid.TryParse(userId, out _),
                persistedDefinitionValidator: AgentTaskManagementService.ValidatePersistedSchedule));
        serviceCollection.AddScoped<AgentTaskRepository>(sp =>
            new AgentTaskRepository(
                () => PathUtil.UsersPath,
                sp.GetRequiredService<IAgentTaskCatalog>(),
                sp.GetRequiredService<YamlConverter>(),
                sp.GetRequiredService<ILogger<AgentTaskRepository>>(),
                sp.GetRequiredService<FileSessionIndexCoordinator>(),
                sp.GetRequiredService<IContentProtector>(),
                sp.GetRequiredService<IRedactionService>(),
                WellKnownTasks.ReservedFolderKeys));
        serviceCollection.AddScoped<IAgentTaskRepository>(sp => sp.GetRequiredService<AgentTaskRepository>());
        serviceCollection.AddScoped<IAgentTaskExecutor, AgentTaskExecutor>();
        serviceCollection.AddSingleton<SkillImportService>(sp => new SkillImportService(
            userId => sp.GetRequiredService<IUserDirectoryService>().GetSkillsDirectoryPath(userId),
            sp.GetRequiredService<ILogger<SkillImportService>>()));
        serviceCollection.AddSingleton<SkillGovernanceService>(sp => new SkillGovernanceService(
            sp.GetRequiredService<Oscova.Agents.Core.Interfaces.IUserPermissionService>(),
            sp.GetRequiredService<IAppSettingsRepository>(),
            sp.GetRequiredService<IContentProtector>(),
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Oscova.Agents.Infrastructure.Data.ApplicationDbContext>>(),
            sp.GetRequiredService<Oscova.Agents.Core.Auth.ExecutionPermissionRegistry>(),
            sp.GetRequiredService<SkillImportService>(),
            userId => sp.GetRequiredService<IUserDirectoryService>().GetSkillsDirectoryPath(userId),
            sp.GetRequiredService<ILoggerFactory>()));
        serviceCollection.AddScoped<ISkillImportService, GovernedSkillImporter>();
        serviceCollection.AddSingleton(sp => new GitHubSkillRepositoryCache(
            PathUtil.SkillRegistryCachePath,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<GitHubSkillRepositoryCache>>()));
        serviceCollection.AddScoped<IGitHubSkillRegistryService, GitHubSkillRegistryService>();
        serviceCollection.AddSingleton<GitHubSkillApiCache>();
        serviceCollection.AddScoped<IGitHubSkillInstallService>(sp => new GitHubSkillInstallService(
            sp.GetRequiredService<IGitHubSkillRegistryService>(), sp.GetRequiredService<SkillImportService>(),
            sp.GetRequiredService<IUserPermissionService>(), sp.GetRequiredService<SkillRegistrySettings>(),
            sp.GetRequiredService<ILogger<GitHubSkillInstallService>>()));
        serviceCollection.AddScoped<IMcpRegistryService, McpRegistryService>();
        serviceCollection.AddScoped<IPluginRegistryService, NuGetPluginRegistryService>();
        serviceCollection.AddScoped<IPluginInstallService>(sp =>
        {
            var environment = sp.GetService<IHostEnvironment>();
            // Development startup can install unpublished plugins from Visual Studio's local feeds.
            var localSources = environment?.IsDevelopment() == true
                ? new PackageSourceProvider(NuGet.Configuration.Settings.LoadDefaultSettings(environment.ContentRootPath))
                    .LoadPackageSources().Where(source => source.IsEnabled && source.IsLocal).ToArray()
                : [];
            // A controlled update feed holds only Oscova packages, so the official feed still restores third-party dependencies.
            var testSource = UpdateTestSources.GetPackageDirectory();
            return new NuGetPluginInstallService(sp.GetRequiredService<ILogger<NuGetPluginInstallService>>(),
                NuGetApplicationUpdateSource.OfficialFeedUrl, PathUtil.PluginsPath,
                testSource is null ? localSources : [new PackageSource(testSource), .. localSources]);
        });
        serviceCollection.AddSingleton<PendingPluginUpdateBootstrapper>();
        serviceCollection.AddScoped<DefaultPluginInstaller>();
        serviceCollection.AddSingleton<EnvironmentBackupService>();
        serviceCollection.AddSingleton<NuGetApplicationUpdateSource>();
        serviceCollection.AddScoped<CommandHookExecutor>();
        serviceCollection.AddScoped<HttpHookExecutor>();
        serviceCollection.AddScoped<IHookApiKeyService, HookApiKeyService>();
        serviceCollection.AddScoped<IAgentHookDispatcher, AgentHookDispatcher>();
        serviceCollection.AddSingleton<IAgentRuntimeHookService, AgentRuntimeHookService>();
        serviceCollection.AddSingleton<IToolQuotaUsageStore, FileToolQuotaUsageStore>();

        return serviceCollection;
    }

    private static string ResolveSharedContextRoot(IUserDirectoryService userDirectories, string userId)
    {
        var userRoot = Path.GetFullPath(userDirectories.GetUserDirectoryPath(userId));
        var contextRoot = Path.GetFullPath(Path.Combine(
            userRoot,
            UserFileNames.AgentsDirectory,
            UserFileNames.SharedAgentsDirectory));
        if (!PathHelper.IsPathWithinDirectory(contextRoot, userRoot) ||
            PathHelper.HasSymlinkInPath(contextRoot, userRoot))
        {
            throw new UnauthorizedAccessException("The shared-agent context root is outside the user directory.");
        }

        return contextRoot;
    }

    /// <summary>
    /// Builds credential metadata while keeping plugin credential declaration errors non-fatal at startup.
    /// </summary>
    internal static CredentialTypeRegistry BuildCredentialTypeRegistry(
        IAgentPluginCatalog pluginCatalog,
        OAuthProviderCatalog accountCatalog,
        ILogger logger)
    {
        var builtInProviderTypes = new[]
        {
            typeof(OpenAiModelProvider),
            typeof(GroqModelProvider),
            typeof(DeepSeekModelProvider),
            typeof(OpenAiCompatibleModelProvider),
            typeof(OpenRouterModelProvider),
            typeof(NvidiaNimModelProvider),
            typeof(AnthropicModelProvider),
            typeof(GoogleModelProvider)
        };
        return CredentialTypeRegistry.Build(BuiltInCredentialToolsetTypes.Concat(builtInProviderTypes))
            .WithPlugins(pluginCatalog, accountCatalog, logger);
    }

    /// <summary>Initializes plugin provider contributions before runtime catalogs are resolved.</summary>
    public static Task InitializePluginProvidersAsync(this IServiceProvider services, CancellationToken ct)
        => services.GetRequiredService<PluginProviderActivation>().InitializeAsync(ct);
}
