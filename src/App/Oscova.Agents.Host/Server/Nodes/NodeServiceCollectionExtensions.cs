using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Core.Llm;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Notifications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Host.Server.Security;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Host.Server.Nodes;

/// <summary>Composes device transport and account permission enforcement in the central host.</summary>
public static class NodeServiceCollectionExtensions
{
    /// <summary>Registers the existing transport with dedicated node credentials and bounded hub messages.</summary>
    public static IServiceCollection AddDeviceNodes(this IServiceCollection services)
    {
        services.AddSingleton<ExecutionPermissionRegistry>(provider =>
        {
            var executions = new ExecutionPermissionRegistry();
            executions.ConfigurationInterrupted += async users =>
            {
                var scopes = provider.GetRequiredService<UserServiceManager>();
                var notifications = provider.GetRequiredService<NotificationHub<NotificationMessage>>();
                var localizer = provider.GetRequiredService<IServerTextLocalizer>();
                foreach (var userId in users)
                {
                    var culture = scopes.TryGetExistingScope(userId, out var scope)
                        ? scope.UiSettings.CultureName
                        : UiCultureCatalog.DefaultCultureName;
                    await notifications.NotifyAsync(userId, new NotificationMessage(
                        localizer.Localize(culture, NotificationTextKeys.ExecutionInterruptedTitle, "Work interrupted"),
                        localizer.Localize(culture, NotificationTextKeys.ExecutionInterruptedBody,
                            "Your affected work is stopping because its settings changed. Review the settings before starting a new run."),
                        NotificationKind.SystemWarning,
                        UserId: userId)).ConfigureAwait(false);
                }
            };
            executions.ConfigurationChanged += async key =>
            {
                await provider.GetRequiredService<ExecutionEnvironmentCatalog>().StopAsync(
                    key == ExecutionSettings.ConfigurationKey ? null : key).ConfigureAwait(false);
                await provider.GetRequiredService<NodeBroker>().StopProviderAsync(key).ConfigureAwait(false);
                var scopes = provider.GetRequiredService<UserServiceManager>();
                foreach (var userId in scopes.ActiveUserIds)
                {
                    if (scopes.TryGetExistingScope(userId, out var scope) &&
                        scope.ModelProviderSettings.ModelProviderConfigs.Any(config => ModelProviderExecution.Key(config.Id) == key))
                    {
                        scope.AppService.NotifyModelProviderSettingsChanged();
                    }
                }
            };
            return executions;
        });
        services.AddSingleton<IUserPermissionService, UserPermissionService>();
        services.AddSingleton<NodeCredentialStore>();
        services.AddSingleton<NodeProjectExecutor>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, NodeAuthenticationHandler>(NodeAuthenticationHandler.SchemeName, _ => { });
        services.AddSignalR().AddHubOptions<NodeHub>(options =>
        {
            options.MaximumReceiveMessageSize = NodeProtocol.MaxTransportMessageBytes;
            // Input requests can wait for a person while other calls on this node obtain admission.
            options.MaximumParallelInvocationsPerClient = 16;
        });

        services.AddSingleton<NodeBroker>(provider =>
        {
            var executions = provider.GetRequiredService<ExecutionPermissionRegistry>();
            var broker = new NodeBroker(provider.GetRequiredService<IHubContext<NodeHub>>(),
                provider.GetRequiredService<NodeCredentialStore>(), provider.GetRequiredService<IUserPermissionService>(),
                provider.GetRequiredService<IContentProtector>(), provider.GetRequiredService<PendingDuressSelections>(),
                provider.GetRequiredService<OperatorVaultAccess>(),
                executions,
                provider.GetRequiredService<ExecutionEnvironmentCatalog>(),
                provider.GetRequiredService<ILogger<NodeBroker>>());
            executions.Changed += broker.ApplyPolicyAsync;
            return broker;
        });
        services.AddSingleton<INodeDispatcher>(provider => provider.GetRequiredService<NodeBroker>());
        services.AddSingleton<NodePluginDeploymentService>();
        services.AddSingleton<NotificationHub<NodePluginStatus>>();
        services.AddSingleton<INodePluginExecutor>(provider => provider.GetRequiredService<NodePluginDeploymentService>());
        services.AddHostedService(provider => provider.GetRequiredService<NodePluginDeploymentService>());
        return services;
    }
}
