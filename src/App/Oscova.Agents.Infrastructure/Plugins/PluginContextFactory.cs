using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Nodes;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core;
using Oscova.Agents.Secrets;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.OAuth;
using Oscova.Agents.Infrastructure.OAuth;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Plugins;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Browsing;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Settings;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Channels;
using Oscova.Agents.Infrastructure.Chat;

namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Builds plugin contexts without duplicating user ownership and service wiring.</summary>
public sealed class PluginContextFactory(
    IHttpClientFactory clients, IHttpMessageHandlerFactory handlers, IDownloadService downloads,
    ILoggerFactory loggers, OAuthProviderCatalog providers, IToolOutputStore outputStore,
    IContentProtector protector, IUserDirectoryService directories,
    RunContextAccessor runs, IAppSettingsRepository settings, IUserPermissionService permissions, PluginActivationSettings activation,
    ExecutionPermissionRegistry executions, ChannelCatalog channels)
{
    /// <summary>Creates a plugin context with encryption and paths bound to the supplied user when present.</summary>
    public AgentPluginContext Create(LoadedAgentPlugin plugin, IToolCredentialResolver credentials,
        TimeProvider clock, IOAuthTokenService? tokens = null, string? userId = null,
        Func<CancellationToken, Task<BrowserSessionScope>>? resolveBrowserSession = null,
        Func<BrowserCommand, CancellationToken, Task<BrowserResult>>? executeBrowserCommand = null,
        Func<bool, CancellationToken, Task>? requireUserSettings = null)
    {
        var userDirectory = userId is null ? null : directories.GetUserDirectoryPath(userId);
        return new(credentials,
            new PluginHostServices(plugin.Manifest.Id, clients, handlers, downloads, providers, tokens,
                outputStore, protector, userId),
            loggers, clock, plugin.PluginDirectory, PathUtil.ModelsPath)
        {
            InstallationSettings = new BoundPluginSettings(settings, plugin.Manifest.Id, WellKnownUsers.SystemId,
                (write, ct) => write ? Task.FromException(new UnauthorizedAccessException("Installation settings writes require an interactive operator context.")) : Task.CompletedTask),
            PrivateDataDirectory = Path.Combine(PathUtil.AppDataPath, "plugin-data", plugin.Manifest.Id.ToLowerInvariant()),
            GetAllowedDirectories = () => runs.Current?.AllowedDirectories ?? [],
            ResolveBrowserSessionAsync = resolveBrowserSession,
            ExecuteBrowserCommandAsync = executeBrowserCommand,
            UserDataDirectory = userDirectory,
            UserSettings = userId is null ? null : new BoundPluginSettings(settings, plugin.Manifest.Id, userId,
                requireUserSettings ?? ((_, _) => { RequireUserAccess(plugin, userId); return Task.CompletedTask; })),
            UserSettingsDirectory = userDirectory is null
                ? null : Path.Combine(userDirectory, UserFileNames.SettingsDirectory)
        };
    }

    /// <summary>Checks current activation and user-vault authority for retained runtime capabilities.</summary>
    internal void RequireUserAccess(LoadedAgentPlugin plugin, string userId)
    {
        if (!activation.IsEnabled(plugin.Manifest.Id)) { throw new InvalidOperationException("This plugin is disabled. Restart after enabling it."); }

        if (!protector.IsAvailable(userId)) { throw new ConfidentialDataLockedException("Unlock your Vault Password before using plugin settings."); }
    }

    /// <summary>Creates authenticated settings operations with storage scope selected by the admitted panel.</summary>
    public async Task<PluginSettingsContext> CreateSettingsAsync(LoadedAgentPlugin plugin, AgentSettingsContribution panel,
        UserServiceScope user, IPluginSettingsUi ui, CancellationToken ct,
        Func<string, Func<CancellationToken, Task>, IPluginChannelControl>? channelControl = null)
    {
        async Task Require(CancellationToken token)
        {
            if (!activation.IsEnabled(plugin.Manifest.Id)) { throw new InvalidOperationException("This plugin is disabled. Restart after enabling it."); }

            if (!protector.IsAvailable(user.UserId)) { throw new ConfidentialDataLockedException("Unlock your Vault Password before using plugin settings."); }

            if (panel.Access == PluginSettingsAccess.Operator && !await permissions.IsOperatorAsync(user.UserId, token).ConfigureAwait(false))
            { throw new UnauthorizedAccessException("This settings panel is available only to the current operator."); }
        }

        await Require(ct).ConfigureAwait(false);
        var context = Create(plugin, new ToolCredentialResolverAdapter(user.CredentialService, plugin.DeclaredCredentialKeys, plugin.Manifest.Id),
            new UserTimeProvider(user.UiSettings.GetTimeZone), userId: user.UserId,
            requireUserSettings: (_, token) => Require(token));
        var bound = new BoundPluginSettings(settings, plugin.Manifest.Id,
            panel.Access == PluginSettingsAccess.Operator ? WellKnownUsers.SystemId : user.UserId,
            (_, token) => Require(token));
        return new(context, plugin.Manifest.Id, panel.AssociationId, bound, ui, Require, async token =>
        { await Require(token).ConfigureAwait(false); return user.PrivacySettings.IsLocalOnly; })
        {
            ResolveChannelAsync = channelControl is null ? null : async (channelId, token) =>
            {
                await Require(token).ConfigureAwait(false);
                if (!plugin.Channels.Any(channel => string.Equals(channel.Definition.Id, channelId, StringComparison.OrdinalIgnoreCase)) ||
                    !ReferenceEquals(channels.Get(channelId).Plugin, plugin))
                { throw new UnauthorizedAccessException("The requested channel must be admitted for this plugin."); }

                return channelControl(channelId, Require);
            },
            RegisterOperation = cancel =>
            {
                var key = PluginCapability.ConfigurationKey(plugin.Manifest.Id);
                var version = executions.GetConfigurationVersion(key);
                // A pending settings operation may resume after deactivation completed.
                if (!activation.IsEnabled(plugin.Manifest.Id)) { throw new InvalidOperationException("This plugin is disabled. Restart after enabling it."); }

                var permission = executions.Register(user.UserId,
                    panel.Access == PluginSettingsAccess.Operator ? UserPermissionKeys.All : [], CancellationToken.None,
                    () => { cancel(); return Task.CompletedTask; });
                try
                {
                    var configuration = executions.RegisterConfiguration(key, version, user.UserId, CancellationToken.None,
                        () => { cancel(); return Task.CompletedTask; });
                    return new SettingsOperation(permission, configuration);
                }
                catch { permission.Dispose(); throw; }
            }
        };
    }

    private sealed class SettingsOperation(IDisposable permission, IDisposable configuration) : IDisposable
    {
        public void Dispose() { configuration.Dispose(); permission.Dispose(); }
    }

    /// <summary>Creates a private built-in contribution context.</summary>
    public AgentPluginContext CreateBuiltIn(IToolCredentialResolver credentials, TimeProvider clock)
        => new(credentials, new PluginHostServices(string.Empty, clients, handlers, downloads, providers,
                null, outputStore, protector, userId: null),
            loggers, clock, AppContext.BaseDirectory, PathUtil.ModelsPath);

    /// <summary>Reads the user's current timezone without capturing its value during scope creation.</summary>
    internal sealed class UserTimeProvider(Func<TimeZoneInfo> resolve) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => resolve();
    }
}
