using Microsoft.AspNetCore.Components.Server.Circuits;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Host.Server.Nodes;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using NLog.Web;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json;
using NuGet.Versioning;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Host.Logging;
using Oscova.Agents.Host.Server.Security;
using Oscova.Agents.Host.Server.Hubs;
using Oscova.Agents.Host.Server.Hosting;
using Oscova.Agents.Host.Server;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Updates;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Infrastructure.Speech;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure;
using Oscova.Agents.Infrastructure.Backup;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Plugins.Registry;
using Oscova.Agents.Infrastructure.OAuth;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Services.Realtime;
using Oscova.Agents.Host.Hosting;
using Oscova.Agents.Host.Help;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Host.Services.UI;
using Oscova.Agents.Host.Security;
using Oscova.Agents.Tasks;
using Oscova.Agents.Notifications;
using Oscova.Agents.Settings;
using Oscova.Agents.Plugins;
using Oscova.Agents.Utils;
using Oscova.Agents.Host.Server.Endpoints;

namespace Oscova.Agents.Host;

/// <summary>
/// Shared web host bootstrap. Called by both the Host's own Program.cs entrypoint
/// (when launched via `dotnet run`) and by Oscova.Agents.App (when launched via `oscova start`).
/// </summary>
public static class WebHostStartup
{
    // Host-owned product policy pins the versions built with this host so startup never takes an unreviewed upgrade.
    public const int RequiredPluginCount = 4;
    public static IReadOnlyList<(string PackageId, string PluginId, string Version)> DefaultPlugins { get; } =
    [
        DefaultPlugin("Oscova.Agents.Plugins.AgentTemplates", "oscova.agent-templates"),
        DefaultPlugin("Oscova.Agents.Plugins.Web", "oscova.web"),
        DefaultPlugin("Oscova.Agents.Plugins.Browser", "oscova.browser"),
        DefaultPlugin("Oscova.Agents.Plugins.Mxc", "oscova.mxc"),
        DefaultPlugin("Oscova.Agents.Plugins.Docker", "oscova.docker"),
        DefaultPlugin("Oscova.Agents.Plugins.LlamaSharp", "oscova.llamasharp"),
        DefaultPlugin("Oscova.Agents.Plugins.LocalSpeechRecognition", "oscova.local-speech-recognition"),
        DefaultPlugin("Oscova.Agents.Plugins.LocalSpeechSynthesis", "oscova.local-speech-synthesis"),
        DefaultPlugin("Oscova.Agents.Plugins.OpenAI", "oscova.openai"),
        DefaultPlugin("Oscova.Agents.Plugins.ElevenLabs", "oscova.elevenlabs"),
        DefaultPlugin("Oscova.Agents.Plugins.Discord", "oscova.discord"),
        DefaultPlugin("Oscova.Agents.Plugins.Telegram", "oscova.telegram"),
        DefaultPlugin("Oscova.Agents.Plugins.Slack", "oscova.slack"),
        DefaultPlugin("Oscova.Agents.Plugins.GoogleWorkspace", "oscova.google-workspace"),
        DefaultPlugin("Oscova.Agents.Plugins.MicrosoftGraph", "oscova.microsoft-graph")
    ];

    /// <summary>Describes the optional capabilities in their startup display order.</summary>
    public static IReadOnlyList<StartupPluginOption> OptionalPlugins { get; } = CreateOptionalPlugins();

    private static IReadOnlyList<StartupPluginOption> CreateOptionalPlugins()
    {
        var assembly = typeof(WebHostStartup).Assembly;
        var manifests = new Dictionary<string, AgentPluginManifest>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("PluginManifests.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var manifest = JsonSerializer.Deserialize<AgentPluginManifest>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            manifests.Add(manifest.Id + (resource.EndsWith("oscova-plugin.cuda.json", StringComparison.Ordinal) ? ".cuda" : ""), manifest);
        }

        return DefaultPlugins.Skip(RequiredPluginCount)
            .Where(item => manifests[item.PluginId].SupportsRuntimeIdentifier(AgentPluginLoader.SupportedRuntimeIdentifier))
            .Select(item => CreateOptionalPlugin(item.PluginId) with
            {
                HardwareRequirement = manifests[item.PluginId].HardwareRequirements?.GetValueOrDefault(AgentPluginLoader.SupportedRuntimeIdentifier),
                CudaHardwareRequirement = manifests.GetValueOrDefault(item.PluginId + ".cuda")?.HardwareRequirements?.GetValueOrDefault(AgentPluginLoader.SupportedRuntimeIdentifier)
            }).ToArray();
    }

    private static StartupPluginOption CreateOptionalPlugin(string pluginId)
    {
        var (name, description) = pluginId switch
        {
            "oscova.docker" => ("Docker", "Run agent commands in Docker containers."),
            "oscova.llamasharp" => ("LLamaSharp", "Run local language models in GGUF format."),
            "oscova.local-speech-recognition" => ("Local Speech Recognition", "Turn speech into text locally with Whisper."),
            "oscova.local-speech-synthesis" => ("Local Speech Synthesis", "Read text aloud locally with Kokoro."),
            "oscova.openai" => ("OpenAI", "Transcribe speech and generate spoken audio with OpenAI."),
            "oscova.elevenlabs" => ("ElevenLabs", "Transcribe speech and generate spoken audio with ElevenLabs."),
            "oscova.discord" => ("Discord", "Chat with Oscova through a Discord bot."),
            "oscova.telegram" => ("Telegram", "Chat privately with Oscova through a Telegram bot."),
            "oscova.slack" => ("Slack", "Chat privately with Oscova through a Slack bot."),
            "oscova.google-workspace" => ("Google Workspace", "Access Gmail, Google Calendar and YouTube tools."),
            "oscova.microsoft-graph" => ("Microsoft Graph", "Access Outlook mail and Microsoft To Do tools."),
            _ => throw new InvalidOperationException($"Missing startup description for {pluginId}.")
        };
        var supportsCuda = RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
            (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
            pluginId is "oscova.local-speech-recognition" or "oscova.local-speech-synthesis" or "oscova.llamasharp";
        return new StartupPluginOption(pluginId, name, description, supportsCuda);
    }

    // Versions come from Versions.props through build-time assembly metadata.
    private static (string PackageId, string PluginId, string Version) DefaultPlugin(string packageId, string pluginId) =>
        (packageId, pluginId, typeof(WebHostStartup).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == packageId).Value!);

    public static async Task RunAsync(string[] args, WebHostRunOptions? options = null)
        {
            NativeRuntimeEnvironment.DisableOnnxTelemetry();
            options ??= WebHostRunOptions.Empty;
            using var lifecycleLease = await LocalLifecycleLease.AcquireAsync(
                Path.GetDirectoryName(RuntimeDescriptorWriter.RuntimePath)!, options.StartupCancellationToken).ConfigureAwait(false);
            var appDataPath = PathUtil.AppDataPath;
            Environment.SetEnvironmentVariable(PathUtil.AppDataDirectoryEnvironmentVariable, appDataPath);
            EnsurePrivateAppDataDirectory(appDataPath);
            using var environmentLease = EnvironmentRestoreBootstrapper.AcquireEnvironmentLease();
            var currentVersion = options.ToolInstallation?.PackageVersion ?? new RuntimeVersionService().GetSnapshot().Host.FullVersion;
            var previousVersion = ReportVersionChange(appDataPath, currentVersion, options.StartupVersionChanged);
            var updatePreparation = await ApplicationUpdatePreparation.PrepareEnvironmentAsync(
                previousVersion, currentVersion, new ConfigurationBuilder().AddEnvironmentVariables().Build(),
                options.StartupNotice, options.StartupCancellationToken).ConfigureAwait(false);
            EnvironmentRestoreBootstrapper.ApplyPendingOperationIfPresent();
            ValidateEnvironmentFormat(appDataPath);
            RefreshBundledAppData(appDataPath);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = options.ContentRootPath,
                WebRootPath = options.WebRootPath
            });
            builder.Services.AddSingleton(new OriginalHostArgs(options.OriginalCommandLineArgs ?? args));
            builder.Services.AddSingleton(new ApplicationUpdateHostOptions(
                options.ToolInstallation,
                options.StartConsole));

            // Swap built-in providers for NLog
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Host.UseNLog(new NLogAspNetCoreOptions
            {
                IncludeScopes = true
            }); // reads nlog.config

            // Add services to the container.
            builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
            builder.Services.AddSingleton<IServerTextLocalizer, ServerTextLocalizer>();
            builder.Services.AddSingleton<IElicitationTextLocalizer, ElicitationTextLocalizer>();
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.Configure<RequestLocalizationOptions>(options =>
            {
                options.DefaultRequestCulture = new RequestCulture(UiCultureCatalog.DefaultCultureName);
                options.SupportedCultures = UiCultureCatalog.SupportedCultures.ToList();
                options.SupportedUICultures = UiCultureCatalog.SupportedCultures.ToList();
                options.RequestCultureProviders =
                [
                    new CookieRequestCultureProvider(),
                    new AcceptLanguageHeaderRequestCultureProvider()
                ];
            });

            // Voice mode sends base64 WAV audio over the Blazor SignalR circuit.
            // Default limit is 32KB — increase to allow audio payloads, accounting for base64 overhead.
            builder.Services.AddSignalR(options =>
            {
                // Allow up to 4 MB per message so longer utterances (plus base64 overhead) do not break the circuit.
                options.MaximumReceiveMessageSize = 4 * ByteSize.OneMegabyte; // 4 MB
            });
            builder.Services.AddDeviceNodes();

            builder.Services.AddLogging();
            builder.Services.AddScoped<IChatSpeechService, ChatSpeechService>();
            builder.Services.AddScoped<IAvatarBridgeClient, AvatarBridgeClient>();
            builder.Services.AddScoped<LayoutCustomizerService>();
            builder.Services.AddScoped<IDialogService, DialogService>();
            builder.Services.AddScoped<IToastService, ToastService>();
            builder.Services.AddSingleton<Oscova.Agents.Host.Components.Settings.SettingsPageRegistry>();
            builder.Services.AddSingleton<Oscova.Agents.Host.Services.UI.PluginPresentationService>();
            builder.Services.AddSingleton<Oscova.Agents.Host.Services.UI.PluginSettingsCatalog>();
            builder.Services.AddScoped<Oscova.Agents.Host.Services.UI.PluginSettingsUi>();
            builder.Services.AddScoped<SoundCuePlayer>();
            builder.Services.AddScoped<ChatSidebarState>();
            builder.Services.AddScoped<ChatDraftState>();
            builder.Services.AddScoped<ProfilePictureRefreshState>();
            builder.Services.AddScoped<WallpaperRefreshState>();
            builder.Services.AddScoped<IdleLogoutActivityState>();
            builder.Services.AddScoped<AgentAvatarLookup>();
            builder.Services.AddAgentsServer();
            builder.Services.AddHttpClient();
            builder.Services.AddScoped<ModelTestCircuitHandler>();
            builder.Services.AddScoped<CircuitHandler>(sp => sp.GetRequiredService<ModelTestCircuitHandler>());
            builder.Services.Configure<ForwardedHeadersOptions>(forwarding =>
            {
                forwarding.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarding.ForwardLimit = 1;
                forwarding.KnownIPNetworks.Clear();
                forwarding.KnownProxies.Clear();
                forwarding.KnownProxies.Add(IPAddress.Loopback);
                forwarding.KnownProxies.Add(IPAddress.IPv6Loopback);
            });
            builder.Services.AddSingleton<BlazorAccessTokenService>();
            builder.Services.AddSingleton<LocalControlTokenProvider>();
            builder.Services.AddScoped<CliAccessTokenService>();
            builder.Services.AddScoped<CliAuthFileWriter>();
            builder.Services.AddSingleton<ICliDeviceCodeStore, CliDeviceCodeStore>();
            builder.Services.AddSingleton<HostProcessRelauncher>();
            builder.Services.AddScoped<ApiAccessTokenProvider>();
            builder.Services.AddScoped<ApiAuthorizationHandler>();
            builder.Services.AddScoped<SettingsApiClient>();
            builder.Services.AddScoped<ManagementApiClient>();
            builder.Services.AddScoped<OAuthApiClient>();
            builder.Services.AddScoped<ChatApiClient>();
            builder.Services.AddScoped<NodesApiClient>();

            builder.Services.AddScoped<ProjectsApiClient>();
            builder.Services.AddScoped<ArtifactApiClient>();
            builder.Services.AddScoped<SystemHubClient>();
            builder.Services.AddSingleton<RuntimeVersionService>();
            builder.Services.AddSingleton<ApplicationUpdateService>();
            // Launcher operations stay App-owned while the Host owns admission, discovery and browser progress.
            builder.Services.AddSingleton(new AvatarEngineHostOptions(options.AvatarEngineOperations));
            builder.Services.AddSingleton<AvatarUpdateService>();
            builder.Services.AddSingleton<UpdateDiscoveryService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateDiscoveryService>());
            // Named client for downloads with a long timeout
            builder.Services.AddHttpClient("downloads", client => client.Timeout = TimeSpan.FromHours(2));

            builder.Services.AddDataProtection()
                .UseEphemeralDataProtectionProvider();

            builder.Services.AddScoped<HelpPanelHost>();
            builder.Services.AddSingleton<IHelpContentService, HelpContentService>();

            builder.Services.AddMemoryCache();
            builder.Services.AddScoped<HostingInfoService>();
            builder.Services.AddScoped<BrowserViewerAccess>();

            // Register download service (shared singleton used by speech services and UI)
            builder.Services.AddInfrastructure();
    #if DEBUG
            DebugTestModelProvider.AddWhenSeeded(builder.Services, builder.Configuration);
    #endif
            builder.Services.AddScoped(sp =>
            {
                // Sync-over-async is intentional: DI factories are synchronous by design and this
                // factory only runs inside a Blazor Server circuit after auth state is established,
                // so the await machinery is safe here. See UserServiceManager.GetOrCreateScope.
                var authState = sp.GetRequiredService<AuthenticationStateProvider>()
                    .GetAuthenticationStateAsync()
                    .GetAwaiter()
                    .GetResult();
                var userId = authState.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    throw new InvalidOperationException(
                        "UserServiceScope cannot be resolved before authentication is established. " +
                        "This factory must only run inside an authenticated Blazor Server circuit.");
                }

                var manager = sp.GetRequiredService<UserServiceManager>();
                return manager.GetOrCreateScope(userId);
            });
            builder.Services.AddSingleton(sp =>
            {
                var repository = sp.GetRequiredService<IAppSettingsRepository>();
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                return new AgentTaskSchedulerService(
                    sp,
                    sp.GetRequiredService<NotificationHub<TaskStatusUpdate>>(),
                    sp.GetRequiredService<ILogger<AgentTaskSchedulerService>>(),
                    sp.GetRequiredService<TimeProvider>(),
                    async (task, ct) =>
                    {
                        var userId = task.UserId;
                        // Read the saved flag, not the wizard's pending in-memory edit.
                        var settings = new OnboardingSettings(repository, userId, loggerFactory);
                        var yaml = await repository.GetSettingsAsync(userId, OnboardingSettings.Key, ct)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(yaml))
                        {
                            settings.FromYaml(yaml);
                        }

                        return settings.IsCompleted && await sp.GetRequiredService<UserServiceManager>()
                            .CanScheduleTaskAsync(task, ct).ConfigureAwait(false);
                    },
                    userId => sp.GetRequiredService<Oscova.Agents.Secrets.IContentProtector>().IsAvailable(userId) &&
                        !sp.GetRequiredService<Oscova.Agents.Infrastructure.Secrets.PendingDuressSelections>().Contains(userId));
            });
            builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentTaskSchedulerService>());

            // Provide web host info (root path + public URL composition) to infrastructure without referencing ASP.NET types there
            builder.Services.AddSingleton<IWebHostInfoProvider, WebHostInfoProvider>();

            var connectionString = ApplicationDatabaseConnection.ResolveConnectionString(
                builder.Configuration.GetConnectionString("DefaultConnection"));
            ApplicationDatabaseConnection.EnsureDirectoryExists(connectionString);
            builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString));
            builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

            builder.Services
                .AddIdentity<ApplicationUser, IdentityRole>(options =>
                {
                    options.Password.RequiredLength = 6;
                    options.Password.RequireDigit = false;
                    options.Password.RequireNonAlphanumeric = false;
                    options.Password.RequireUppercase = false;
                    options.Password.RequireLowercase = false;
                    // Ensure we don't allow duplicate emails
                    options.User.RequireUniqueEmail = true;
                })
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddUserManager<DuressAwareUserManager>()
                .AddDefaultTokenProviders();

            // Mirror FullName to ClaimTypes.Name on sign-in for easy display
            builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, AppUserClaimsPrincipalFactory>();

            builder.Services.ConfigureApplicationCookie(options =>
            {
                options.LoginPath = "/login";
                // A signed-in user without access sees a denial, not the sign-in form that would loop back here.
                options.AccessDeniedPath = "/access-denied";
                options.SlidingExpiration = true;
                options.Events.OnSigningIn = BrowserViewerAccess.SigningIn;
                options.Events.OnSigningOut = BrowserViewerAccess.SigningOut;
                options.Events.OnValidatePrincipal = BrowserViewerAccess.ValidatePrincipal;
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
            });

            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = AgentsServerAuthenticationDefaults.SmartScheme;
                options.DefaultChallengeScheme = AgentsServerAuthenticationDefaults.SmartScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, BlazorAccessTokenAuthenticationHandler>(
                AgentsServerAuthenticationDefaults.BlazorBearerScheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, HookApiKeyAuthenticationHandler>(
                AgentsServerAuthenticationDefaults.HookApiKeyScheme,
                _ => { })
            .AddScheme<AuthenticationSchemeOptions, LocalControlAuthenticationHandler>(
                AgentsServerAuthenticationDefaults.LocalControlScheme,
                _ => { })
            .AddPolicyScheme(AgentsServerAuthenticationDefaults.SmartScheme, null, options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    // Blazor API writes use bearer auth; browser-native resource reads and navigation keep cookie auth.
                    var authorization = context.Request.Headers.Authorization.ToString();
                    var hasBearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
                    var isAntiforgeryExemptApi = context.Request.Path.StartsWithSegments("/api") &&
                        context.GetEndpoint()?.Metadata.GetMetadata<IgnoreAntiforgeryTokenAttribute>() is not null;
                    var requiresBearer = isAntiforgeryExemptApi &&
                        !HttpMethods.IsGet(context.Request.Method) &&
                        !HttpMethods.IsHead(context.Request.Method);
                    if (hasBearer || requiresBearer)
                    {
                        return AgentsServerAuthenticationDefaults.BlazorBearerScheme;
                    }

                    return IdentityConstants.ApplicationScheme;
                };
            });

            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(AgentsServerAuthenticationDefaults.BrowserBackupExportPolicy, policy =>
                    policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireRole("Admin"));
                options.AddPolicy(AgentsServerAuthenticationDefaults.HostControlPolicy, policy =>
                {
                    policy.AddAuthenticationSchemes(
                        AgentsServerAuthenticationDefaults.SmartScheme,
                        AgentsServerAuthenticationDefaults.LocalControlScheme)
                        .RequireAssertion(ctx =>
                        ctx.User.IsInRole("Admin") ||
                        ctx.User.HasClaim(LocalControlAuthenticationHandler.LocalControlClaim, "true"));
                });

                options.AddPolicy(AgentsServerAuthenticationDefaults.HostStatusPolicy, policy =>
                {
                    policy.AddAuthenticationSchemes(
                        AgentsServerAuthenticationDefaults.SmartScheme,
                        AgentsServerAuthenticationDefaults.LocalControlScheme)
                        .RequireAssertion(ctx =>
                        (ctx.User.Identity?.IsAuthenticated ?? false) ||
                        ctx.User.HasClaim(LocalControlAuthenticationHandler.LocalControlClaim, "true"));
                });

                options.AddPolicy(AgentsServerAuthenticationDefaults.ApplicationUpdatePolicy, policy =>
                {
                    policy.AddAuthenticationSchemes(AgentsServerAuthenticationDefaults.BlazorBearerScheme)
                        .RequireAuthenticatedUser()
                        .RequireRole("Admin");
                });
            });

            // Use cookie-based auth state with Blazor Server
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddHttpContextAccessor();

            // HttpClient for Blazor Server components and services.
            // UserServiceManager may create scoped services at process startup, before any Blazor
            // circuit exists, so NavigationManager is not safe here.
            builder.Services.AddScoped(sp =>
            {
                var authHandler = ActivatorUtilities.CreateInstance<ApiAuthorizationHandler>(sp);
                authHandler.InnerHandler = new HttpClientHandler();
                var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
                if (httpContext is not null)
                {
                    // Calls go to the HTTP listener socket the request arrived on, because behind Docker port mapping
                    // or a proxy the browser's origin is often unreachable from inside the server.
                    var request = httpContext.Request;
                    var connection = httpContext.Connection;
                    var localAddress = connection.LocalIpAddress is { IsIPv4MappedToIPv6: true } mapped
                        ? mapped.MapToIPv4()
                        : connection.LocalIpAddress;
                    // The in-memory test server has no socket, and a TLS certificate names the request host, so both keep the request origin.
                    var origin = localAddress is not null && httpContext.Features.Get<ITlsConnectionFeature>() is null
                        ? new UriBuilder(Uri.UriSchemeHttp, localAddress.ToString(), connection.LocalPort).Uri.GetLeftPart(UriPartial.Authority)
                        : $"{request.Scheme}://{request.Host}";
                    return new HttpClient(authHandler)
                    {
                        BaseAddress = new Uri($"{origin}{request.PathBase}/")
                    };
                }

                // A circuit without a request, such as one on long polling, calls the listener startup bound.
                var listener = sp.GetRequiredService<NetworkUrlService>().GetSelfCallUrl() ?? "http://localhost";
                return new HttpClient(authHandler)
                {
                    BaseAddress = new Uri($"{listener}/")
                };
            });

            await using var app = builder.Build();
            using var startupCancellation = options.StartupCancellationToken.Register(app.Lifetime.StopApplication);

            using (var scope = app.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var dbContext = services.GetRequiredService<ApplicationDbContext>();
                var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebHostStartup).FullName!);
                await DatabaseStartup.MigrateAsync(dbContext, logger, options.StartupCancellationToken).ConfigureAwait(false);

                var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
                foreach (var role in new[] { "Admin", "Moderator", "User" })
                {
                    if (!roleManager.RoleExistsAsync(role).GetAwaiter().GetResult())
                    {
                        roleManager.CreateAsync(new IdentityRole(role)).GetAwaiter().GetResult();
                    }
                }
            }

            await ReconcileUserDirectoriesAsync(
                app.Services,
                app.Lifetime.ApplicationStopping).ConfigureAwait(false);

            // Pending replacements must become canonical before any plugin-backed setting resolves the catalog.
            app.Services.GetRequiredService<PendingPluginUpdateBootstrapper>().ApplyPendingUpdates();
            await UpdatePluginsForApplicationUpdateAsync(app, updatePreparation).ConfigureAwait(false);

            var networkSettings = app.Services.GetRequiredService<NetworkSettings>();
            await networkSettings.LoadAsync().ConfigureAwait(false);
            await app.Services.GetRequiredService<ExecutionSettings>().LoadAsync().ConfigureAwait(false);
            await app.Services.GetRequiredService<HostOwnershipSettings>().LoadAsync().ConfigureAwait(false);
            await app.Services.GetRequiredService<ModelDownloadSettings>().LoadAsync().ConfigureAwait(false);
            await app.Services.GetRequiredService<SkillRegistrySettings>().LoadAsync().ConfigureAwait(false);

            // Load the plugin allowlist before any service can resolve and activate plugin assemblies.
            var pluginSettings = app.Services.GetRequiredService<PluginActivationSettings>();
            await pluginSettings.LoadAsync().ConfigureAwait(false);

            if (!app.Environment.IsEnvironment("IntegrationTests"))
            {
                await using var scope = app.Services.CreateAsyncScope();
                Task InstallDefaultsAsync(IReadOnlyList<(string PackageId, string PluginId, string Version)> selections,
                    Action<DefaultPluginInstallProgress>? report) =>
                    scope.ServiceProvider.GetRequiredService<DefaultPluginInstaller>()
                        .InstallMissingAsync(selections, PathUtil.PluginsPath, report,
                            app.Lifetime.ApplicationStopping);
                async Task InstallSelectionAsync(IReadOnlyList<(string PackageId, string PluginId, string Version)> selections)
                {
                    if (options.StartupComponentInstallation is { } renderInstallation)
                    {
                        await renderInstallation(report => InstallDefaultsAsync(selections, report)).ConfigureAwait(false);
                    }
                    else { await InstallDefaultsAsync(selections, null).ConfigureAwait(false); }
                }

                // Required installs never add optional plugins, so one inspection serves both groups and the offer below.
                var installedIds = AgentPluginLoader.InspectDirectory(PathUtil.PluginsPath)
                    .Where(item => item.Manifest is not null).Select(item => item.Manifest!.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                await InstallSelectionAsync([.. DefaultPlugins.Take(RequiredPluginCount),
                    .. DefaultPlugins.Skip(RequiredPluginCount).Where(item => installedIds.Contains(item.PluginId))]).ConfigureAwait(false);
                if (options.StartupPluginSelection is { } choosePlugins && updatePreparation.Operation is null)
                {
                    var offered = OptionalPlugins.Where(item => !installedIds.Contains(item.PluginId) && !pluginSettings.IsRemoved(item.PluginId)).ToArray();
                    if (offered.Length > 0)
                    {
                        // Collect and validate every edition before installing or recording declined capabilities.
                        var selected = await choosePlugins(offered, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
                        app.Lifetime.ApplicationStopping.ThrowIfCancellationRequested();
                        StartupPluginSelection.Validate(offered, selected);
                        var choices = selected.ToDictionary(item => item.PluginId, StringComparer.OrdinalIgnoreCase);
                        foreach (var item in offered.Where(item => !choices.ContainsKey(item.PluginId)))
                        {
                            pluginSettings.SetRemoved(item.PluginId, true);
                        }

                        await pluginSettings.SaveAsync().ConfigureAwait(false);
                        var packages = DefaultPlugins.Where(item => choices.ContainsKey(item.PluginId))
                            .Select(item => (choices[item.PluginId].UseCuda ? item.PackageId + ".Cuda" : item.PackageId, item.PluginId, item.Version)).ToArray();
                        await InstallSelectionAsync(packages).ConfigureAwait(false);
                    }
                }
            }

            await PluginFrameworkCompatibility.AdmitAsync(PathUtil.PluginsPath, pluginSettings, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
            options.StartupPluginResults?.Invoke(pluginSettings.CompatibilityDisabledReasons);
            updatePreparation.RecordPluginOutcomes(pluginSettings);

            // Claim account providers and admit plugin providers before optional CLI onboarding.
            app.Services.GetRequiredService<IAgentPluginCatalog>();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await app.Services.GetRequiredService<OAuthProviderCatalog>().InitializeAsync(
                    scope.ServiceProvider.GetRequiredService<ISystemCredentialService>(), app.Lifetime.ApplicationStopping)
                    .ConfigureAwait(false);
            }

            await app.Services.InitializePluginProvidersAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);

            // Run optional CLI onboarding only after required components finish installing.
            if (options.StartupComponentsReady is not null)
            {
                await options.StartupComponentsReady(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
            }

            var activeBinding = ApplyNetworkBinding(app, networkSettings, options.BindingOverride);
            app.Services.GetRequiredService<WebHostRuntimeState>()
                .Update(app.Urls, activeBinding);

            await app.Services.GetRequiredService<UserServiceManager>().InitializeAsync().ConfigureAwait(false);
            await app.Services.GetRequiredService<AuthSettings>().LoadAsync().ConfigureAwait(false);
            await app.Services.GetRequiredService<AvatarSettings>().LoadAsync().ConfigureAwait(false);
            // Clear TTS cache on startup to prevent serving stale audio
            // We don't have a robust cache invalidation strategy yet.
            await app.Services.GetRequiredService<SpeechClipStore>().ClearAllAsync().ConfigureAwait(false);

            // Restore the client address and scheme from a local proxy before security checks.
            app.UseForwardedHeaders();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            // SignalR closes its connections when stopping starts, but Kestrel keeps accepting until it stops.
            // Refuse reconnects then so a new hub connection cannot hold shutdown open for the full timeout.
            var stopping = app.Lifetime.ApplicationStopping;
            app.Use(async (context, next) =>
            {
                if (!stopping.IsCancellationRequested)
                {
                    await next(context);
                    return;
                }

                context.Response.Headers.Connection = "close";
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            });

            // Browser page loads of unknown URLs get the not-found page.
            // Kept at the top level because a UseWhen branch drops the route builder that re-execution needs.
            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.Use((context, next) =>
            {
                // API and CLI clients read error bodies, so only browser page requests get the HTML page.
                if (!context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    context.Features.Get<IStatusCodePagesFeature>()!.Enabled = false;
                }

                return next(context);
            });

            // Register MIME types not known to ASP.NET Core by default (required for self-hosted VAD assets)
            var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            contentTypeProvider.Mappings[".onnx"] = "application/octet-stream";
            contentTypeProvider.Mappings[".mjs"] = "text/javascript";

            // Browsers revalidate every static file by ETag, so app updates reach CSS and JavaScript without a manual cache-buster.
            app.UseStaticFiles(new StaticFileOptions
            {
                ContentTypeProvider = contentTypeProvider,
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
            });

            app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);

            app.UseAntiforgery();

            app.UseWebSockets();

            app.UseAuthentication();
    #if DEBUG
            app.UseMiddleware<DebugAutoLoginMiddleware>();
    #endif
            app.UseAuthorization();
            app.UseMiddleware<Oscova.Agents.Host.Server.Security.VaultGateMiddleware>();
            app.UseMiddleware<UserScopeLoggingMiddleware>();

            // Flush SSE events per write; the AG-UI hosting SseFormatter never flushes, so
            // without this Kestrel buffers the whole run and the browser receives it at once.
            app.UseSseAutoFlush("/agent");

            app.MapControllers();
            app.MapChatAgentStream("/agent").RequireAuthorization();
            app.MapHub<NodeHub>(NodeProtocol.HubPath, options =>
            {
                options.ApplicationMaxBufferSize = NodeProtocol.MaxTransportMessageBytes;
                options.TransportMaxBufferSize = NodeProtocol.MaxTransportMessageBytes;
            });

            app.MapHub<SystemHub>("/hubs/system");
            app.MapRazorComponents<Components.App>()
                .AddInteractiveServerRenderMode();

            RuntimeDescriptorWriter? runtimeDescriptor = null;
            try
            {
                options.StartupCancellationToken.ThrowIfCancellationRequested();
                await app.StartAsync(options.StartupCancellationToken).ConfigureAwait(false);
                WriteCurrentEnvironmentFormat(currentVersion);
                var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebHostStartup).FullName!);
                if (updatePreparation.Complete(ex => startupLogger.LogWarning(ex, "Could not remove committed environment restore recovery data.")))
                {
                    // Saved release notices were announced by the previous version and no longer apply.
                    try
                    {
                        await app.Services.GetRequiredService<INotificationInbox>().RemoveSystemAsync(NotificationKind.HarnessUpdate).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        startupLogger.LogWarning(ex, "Could not remove harness update notices after the application update.");
                    }
                }

                // Publish process discovery only after the restored application has reached readiness.
                var controlToken = app.Services.GetRequiredService<LocalControlTokenProvider>().Token;
                runtimeDescriptor = RuntimeDescriptorWriter.WriteOnStart(app, controlToken);
                lifecycleLease.Dispose();
                options.StartupReady?.Invoke(app.Urls.ToArray());
                startupCancellation.Dispose();
                await Task.Delay(Timeout.Infinite, app.Lifetime.ApplicationStopping)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            finally
            {
                await app.StopAsync().ConfigureAwait(false);
                runtimeDescriptor?.Delete();
            }
        }

    // Plugin installs need the built service provider, so this runs after the environment checkpoint.
    // Integration tests stay offline unless they supply a controlled update feed.
    private static async Task UpdatePluginsForApplicationUpdateAsync(WebApplication app, ApplicationUpdatePreparation preparation)
    {
        if (preparation.Operation is null ||
            (app.Environment.IsEnvironment("IntegrationTests") && UpdateTestSources.GetPackageDirectory() is null))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        await preparation.UpdatePluginsAsync(
            scope.ServiceProvider.GetRequiredService<IPluginRegistryService>(),
            scope.ServiceProvider.GetRequiredService<IPluginInstallService>(),
            scope.ServiceProvider.GetRequiredService<PendingPluginUpdateBootstrapper>(),
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebHostStartup).FullName!),
            app.Lifetime.ApplicationStopping).ConfigureAwait(false);
    }

    private static void EnsurePrivateAppDataDirectory(string path)
    {
        try
        {
            CliFileSecurity.EnsurePrivateDirectory(path);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                $"Oscova cannot secure application data directory '{path}'. " +
                "Choose a location that supports current-user-only file permissions.",
                ex);
        }
    }

    /// <summary>
    /// Reports the previous release before any startup preparation changes its environment.
    /// Returns the previous version when it differs from this version.
    /// </summary>
    private static string? ReportVersionChange(string appDataPath, string currentVersion, Action<string>? report)
    {
        var markerPath = Path.Combine(appDataPath, EnvironmentDataFormat.FileName);
        string? previousVersion = null;
        try
        {
            if (File.Exists(markerPath))
            {
                previousVersion = JsonSerializer.Deserialize<EnvironmentDataFormat>(File.ReadAllText(markerPath), JsonSerializerOptions.Web)?.AppVersion;
            }
        }
        catch (JsonException) { }

        if (!NuGetVersion.TryParse(previousVersion, out var previous) || !NuGetVersion.TryParse(currentVersion, out var current))
        {
            if (File.Exists(markerPath) || File.Exists(Path.Combine(appDataPath, "app.db")) || Directory.Exists(Path.Combine(appDataPath, "users")))
            {
                report?.Invoke($"Previous Oscova version is unavailable. Preparing your existing environment with {currentVersion}.");
            }

            return null;
        }

        var comparison = current.CompareTo(previous);
        if (comparison != 0)
        {
            report?.Invoke($"Detected Oscova version change from {previousVersion} to {currentVersion}. Preparing your existing environment.");
        }

        return comparison != 0 ? previousVersion : null;
    }

    /// <summary>Rejects unsupported persisted environments before startup can prepare or stamp them current.</summary>
    private static void ValidateEnvironmentFormat(string appDataPath)
    {
        var path = Path.Combine(appDataPath, EnvironmentDataFormat.FileName);
        if (!File.Exists(path)) { return; }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("dataFormatVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var dataFormatVersion) ||
                dataFormatVersion != EnvironmentDataFormat.CurrentVersion)
            {
                throw new InvalidDataException($"The environment data format is unsupported. Use a compatible application or restore a compatible backup. Environment: {appDataPath}");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The environment format marker is invalid. Restore a compatible marker or backup. Environment: {appDataPath}", exception);
        }
    }
    private static void WriteCurrentEnvironmentFormat(string appVersion)
    {
        var path = Path.Combine(PathUtil.AppDataPath, EnvironmentDataFormat.FileName);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new EnvironmentDataFormat
                {
                    DataFormatVersion = EnvironmentDataFormat.CurrentVersion,
                    AppVersion = appVersion
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Refreshes shipped help and seed images in the active environment.</summary>
    private static void RefreshBundledAppData(string appDataPath)
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "app_data"));
        var destinationRoot = Path.GetFullPath(appDataPath);
        if (PathHelper.ArePathsEqual(sourceRoot, destinationRoot))
        {
            return;
        }

        CopyGroup("help", "*.md");
        CopyGroup(Path.Combine("seed", "profile-pictures"), "*.png");
        CopyGroup(Path.Combine("seed", "wallpapers"), "*.webp");

        void CopyGroup(string relativePath, string searchPattern)
        {
            var sourceDirectory = Path.GetFullPath(Path.Combine(sourceRoot, relativePath));
            var destinationDirectory = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
            if (!PathHelper.IsPathWithinDirectory(sourceDirectory, sourceRoot) ||
                !PathHelper.IsPathWithinDirectory(destinationDirectory, destinationRoot))
            {
                throw new IOException($"Bundled application data path '{relativePath}' is outside its expected root.");
            }

            ThrowIfReparsePoint(sourceDirectory);
            if (PathHelper.HasSymlinkInPath(destinationDirectory, destinationRoot))
            {
                throw new IOException($"Bundled application data path '{destinationDirectory}' cannot be a link.");
            }

            if (Directory.Exists(destinationDirectory))
            {
                ThrowIfReparsePoint(destinationDirectory);
            }
            else
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                ThrowIfReparsePoint(sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
                if (File.Exists(destinationPath))
                {
                    ThrowIfReparsePoint(destinationPath);
                }

                var temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(sourcePath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.Copy(sourcePath, temporaryPath);
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
        }

        static void ThrowIfReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Bundled application data path '{path}' cannot be a link.");
            }
        }
    }

    private static async Task ReconcileUserDirectoriesAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var liveUserIds = (await dbContext.Users
            .AsNoTracking()
            .Select(user => user.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usersRoot = Path.GetFullPath(PathUtil.UsersPath);
        if (!Directory.Exists(usersRoot))
        {
            return;
        }

        var userDirectoryService = scope.ServiceProvider.GetRequiredService<IUserDirectoryService>();
        var removedDirectoryCount = 0;
        var removedFileCount = 0;
        foreach (var directoryPath in Directory.EnumerateDirectories(usersRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(directoryPath);
            if (!Guid.TryParse(directory.Name, out _) || liveUserIds.Contains(directory.Name))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(directory.FullName);
            var relativePath = Path.GetRelativePath(usersRoot, fullPath);
            if (!string.Equals(relativePath, directory.Name, StringComparison.Ordinal) ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    $"Refusing to remove unsafe orphaned user directory '{directory.FullName}'.");
            }

            // Identity owns user lifetime, so stale roots must disappear before file-backed catalogs load.
            removedFileCount += await userDirectoryService
                .DeleteUserRootAsync(directory.Name, cancellationToken)
                .ConfigureAwait(false);
            removedDirectoryCount++;
        }

        if (removedDirectoryCount > 0)
        {
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(WebHostStartup).FullName!)
                .LogInformation(
                    "Removed {DirectoryCount} orphaned user directories containing {FileCount} files during startup.",
                    removedDirectoryCount,
                    removedFileCount);
        }
    }

    private static NetworkBindingOverride ApplyNetworkBinding(
        WebApplication app,
        NetworkSettings settings,
        NetworkBindingOverride? bindingOverride)
    {
        var port = NetworkSettings.NormalizePort(bindingOverride?.Port ?? settings.Port);
        var host = !string.IsNullOrWhiteSpace(bindingOverride?.Host)
            ? bindingOverride.Host.Trim()
            : settings.EnableNetworkAccess ? "0.0.0.0" : "localhost";

        app.Urls.Clear();
        app.Urls.Add($"http://{host}:{port}");
        return new NetworkBindingOverride(host, port);
    }
}

/// <summary>Wraps default installation in the foreground CLI progress display.</summary>
public delegate Task ComponentInstallRunner(Func<Action<DefaultPluginInstallProgress>?, Task> install);

public sealed class WebHostRunOptions
{
    public static WebHostRunOptions Empty { get; } = new();
    public NetworkBindingOverride? BindingOverride { get; init; }
    public string? ContentRootPath { get; init; }
    public string? WebRootPath { get; init; }
    public string[]? OriginalCommandLineArgs { get; init; }
    public ApplicationToolIdentity? ToolInstallation { get; init; }
    /// <summary>Cancels unfinished preparation when the owning CLI parent leaves.</summary>
    public CancellationToken StartupCancellationToken { get; init; }
    /// <summary>Opens the visible update console for a target version and reports whether a terminal window opened.</summary>
    public Func<string, CancellationToken, Task<bool>>? StartConsole { get; init; }
    /// <summary>Supplies launcher-owned local Avatar lifecycle operations without a reverse project reference.</summary>
    public IAvatarEngineOperations? AvatarEngineOperations { get; init; }
    /// <summary>Reports a release change before startup mutates the existing environment.</summary>
    public Action<string>? StartupVersionChanged { get; init; }
    /// <summary>Runs default installation within an optional CLI rendering scope.</summary>
    public ComponentInstallRunner? StartupComponentInstallation { get; init; }
    /// <summary>Collects the complete optional capability and edition selections from the owning terminal.</summary>
    public Func<IReadOnlyList<StartupPluginOption>, CancellationToken, Task<IReadOnlyList<StartupPluginSelection>>>? StartupPluginSelection { get; init; }
    /// <summary>Retains installed compatibility exclusions in the startup result.</summary>
    public Action<IReadOnlyDictionary<string, string>>? StartupPluginResults { get; init; }
    /// <summary>Reports installed-package update discovery and actionable lookup failures.</summary>
    public Action<string>? StartupNotice { get; init; }
    /// <summary>Runs optional foreground CLI onboarding after required components are ready.</summary>
    public Func<CancellationToken, Task>? StartupComponentsReady { get; init; }
    /// <summary>Reports the listening addresses after the Host is ready to accept requests.</summary>
    public Action<IReadOnlyList<string>>? StartupReady { get; init; }
}

public sealed record NetworkBindingOverride(string? Host, int? Port);
