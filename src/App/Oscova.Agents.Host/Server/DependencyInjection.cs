using Oscova.Agents.Host.Server.Services;
using Oscova.Agents.Host.Server.Hosting;
using Oscova.Agents.Host.Server.Agents;
using Oscova.Agents.Host.Server.Chat;
using Oscova.Agents.Notifications;
using Microsoft.Extensions.Options;
using Oscova.Agents.Host.Achievements;

namespace Oscova.Agents.Host.Server;

public static class DependencyInjection
{
    public static IMvcBuilder AddAgentsServer(this IServiceCollection services)
    {
        services.AddSingleton<LoginTicketService>();
        services.AddSingleton<ChatSessionService>();
        services.AddScoped<DuressActionExecutor>();
        services.AddSingleton<ChatContextPreflightService>();
        services.AddScoped<ArtifactFileAccessService>();
        services.AddScoped<WorkspaceFileEditingService>();

        services.AddSingleton<WebHostRuntimeState>();
        services.AddSingleton<NetworkUrlService>();
        services.AddSingleton<DashboardSnapshotCache>();
        services.AddHostedService<SignalRNotificationRelay>();
        services.AddHostedService<MetricsAggregationService>();
        services.AddHostedService<AchievementWatcher>();
        services.AddHostedService<IncognitoThreadReaper>();
        services.AddSignalR();

        services.AddAGUIServer();
        services.AddHttpContextAccessor();
        services.AddSingleton<ChatDispatchAgent>();
        services.AddSingleton<ChatThreadAgentSessionStore>();
        services.AddSingleton<NotificationHub<ChatRunSignal>>();
        services.AddSingleton<ChatRunProjectionStore>(provider => new ChatRunProjectionStore(
            provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>(),
            provider.GetRequiredService<NotificationHub<ChatRunSignal>>()));
        services.AddSingleton<ChatRunHost>();
        services.AddHostedService(provider => provider.GetRequiredService<ChatRunHost>());

        return services.AddControllersWithViews()
            .AddApplicationPart(typeof(DependencyInjection).Assembly);
    }
}
