using Oscova.Agents.App.Cli;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using NLog.Web;
using Oscova.Agents.Infrastructure;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Settings;
using System.Net;

namespace Oscova.Agents.App.AvatarMcp;

/// <summary>Composes the loopback Avatar MCP listener without starting the Assistant host.</summary>
public static class AvatarMcpHost
{
    /// <summary>Builds the listener, withholding requests until its owner finishes initialization.</summary>
    public static WebApplication Create(int port, Func<bool> isReady)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        // Inherited URL and Kestrel configuration must not add network listeners.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 64 * 1024;
        });
        builder.Logging.ClearProviders();
        builder.Host.UseNLog(new NLogAspNetCoreOptions { IncludeScopes = true });
        builder.Services.AddSingleton<IAppSettingsRepository>(_ => new FileSettingsRepository(_ => PathUtil.SettingsPath));
        builder.Services.AddAvatarBridgeServices();
        builder.Services.AddSingleton<CliPaths>();
        builder.Services.AddSingleton<CliAuthFileStore>();
        builder.Services.AddSingleton<StartCollisionChecker>();
        builder.Services.AddSingleton<HostApiClient>();
        builder.Services.AddSingleton<AvatarSpeechHostClient>();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithTools<AvatarMcpToolset>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host;
            var origins = context.Request.Headers.Origin;
            var allowedOrigin = origins.Count == 0 || (origins.Count == 1 &&
                (origins[0] == $"http://127.0.0.1:{port}" || origins[0] == $"http://localhost:{port}" ||
                 (port == 80 && (origins[0] == "http://127.0.0.1" || origins[0] == "http://localhost"))));
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                (host.Port ?? 80) != port || !(string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase) || host.Host == "127.0.0.1") ||
                !allowedOrigin)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!isReady())
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        app.MapMcp("/mcp");
        return app;
    }
}
