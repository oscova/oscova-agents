using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Oscova.Agents.Infrastructure.Updates;
using System.Net;

namespace Oscova.Agents.App.Cli;

public static class CliServiceCollectionExtensions
{
    public static IServiceCollection AddOscovaCliServices(this IServiceCollection services, string[] originalArgs)
    {
        UpdateTestSources.ValidateAppEnvironment();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(new OriginalCommandLineArgs(originalArgs));
        var paths = new CliPaths();
        paths.SelectAppDataDirectory();
        paths.DeletePromptHistory();
        services.AddSingleton(paths);
        services.AddSingleton<CliConfigStore>();
        services.AddSingleton<CliAuthFileStore>();
        services.AddSingleton<CliHostResolver>();
        services.AddSingleton<HostApiClient>();
        services.AddSingleton<LocalHostControl>();
        services.AddSingleton<EnvironmentEraseOperation>();
        services.AddSingleton<CliSurfaceInspector>();
        services.AddSingleton<CliAskRunner>();
        services.AddSingleton<CliDeviceLoginFlow>();
        services.AddSingleton<AvatarEngineDownload>();
        services.AddSingleton<AvatarEngineManager>();
        services.AddSingleton<AvatarMcpManager>();
        services.AddSingleton<DetachedHostLauncher>();
        services.AddSingleton<StartCollisionChecker>();
        services.AddSingleton<RemoteHostStartPrompt>();
        services.AddSingleton<ApplicationToolInstallation>();
        services.AddSingleton<NuGetApplicationUpdateSource>();
        services.AddHttpClient(AvatarEngineDownload.ClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(HostApiClient.BuildUserAgent());
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None
            })
            .RemoveAllLoggers();
        return services;
    }
}

public sealed record OriginalCommandLineArgs(string[] Args);
