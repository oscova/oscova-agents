using System.Diagnostics;
using Microsoft.Extensions.Options;
using System.Net;
using Oscova.Agents.Infrastructure.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Infrastructure.Mcp;
using Oscova.Agents.Infrastructure.Privacy;
using System.ComponentModel;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Host.Server.Nodes;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Infrastructure.Projects;
using Oscova.Agents.Infrastructure.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

/// <summary>Enrolls this device and runs its capabilities in the foreground or a detached process.</summary>
public sealed class NodeCommand(CliHostResolver resolver, CliDeviceLoginFlow login,
    CliAuthFileStore auth, HostApiClient api, CliPaths paths) : AsyncCommand<NodeCommand.Settings>
{
    /// <summary>Configures the server, first enrollment name and explicit HTTP consent.</summary>
    public sealed class Settings : OscovaCommandSettings
    {
        [CommandArgument(0, "[ACTION]")]
        [Description("Use status or stop to control this computer's node. Omit to connect.")]
        public string? Action { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Device name for first enrollment, 1 to 100 characters. Defaults to this computer's name.")]
        public string? Name { get; init; }

        [CommandOption("--allow-http")]
        [Description("Allow unencrypted HTTP to the chosen server. Commands, credentials and file contents are unencrypted.")]
        public bool AllowHttp { get; init; }

        [CommandOption("--enroll")]
        [Description("Approve sign-in again and replace this account's saved device enrollment. Existing projects keep their previous device identity.")]
        public bool Enroll { get; init; }

        [CommandOption("--detach")]
        [Description("Keep this node running after the terminal closes. Restart it manually after reboot.")]
        public bool Detach { get; init; }

        [CommandOption("--startup-pipe <NAME>", IsHidden = true)]
        public string? StartupPipe { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        CliOutput.Configure(settings);
        if (settings.Action is not null)
        {
            if (settings.Action is not ("status" or "stop")) { throw new CliExitException(4, "Node action must be status or stop. Omit it to connect."); }

            return await ControlAsync(settings, ct).ConfigureAwait(false);
        }

        using var pipe = settings.StartupPipe is null ? null : await CliStartupPipe.ConnectChildAsync(settings.StartupPipe, ct).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, pipe?.ParentDisconnected ?? CancellationToken.None);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try { return await ExecuteCoreAsync(settings, cancellation, pipe).ConfigureAwait(false); }
        catch (Exception exception) when (pipe is not null)
        {
            try
            {
                await pipe.SendAsync(new(StartupMessageKind.Failed, exception.Message), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception) { /* A disconnected parent already owns child cleanup. */ }

            throw;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    /// <summary>Enrolls the device when needed and runs its capabilities until terminal cancellation.</summary>
    private async Task<int> ExecuteCoreAsync(Settings settings, CancellationTokenSource cancellation, CliStartupPipe? startup)
    {
        var ct = cancellation.Token;
        var host = await resolver.ResolveAsync(settings, requireToken: false, ct).ConfigureAwait(false);
        var uri = new Uri(host.HostUrl);
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new CliExitException(4, "The node host must use HTTP or HTTPS.");
        }

        if (uri.Scheme == "http" && !settings.AllowHttp)
        {
            throw new CliExitException(4, "This server uses unencrypted HTTP. Add --allow-http to accept it, or use an HTTPS server URL.");
        }

        var name = (settings.Name ?? Environment.MachineName).Trim();
        if (name.Length is < 1 or > 100) { throw new CliExitException(4, "Device name must contain 1 to 100 characters."); }

        var stored = host.AuthEntry is null ? null
            : await auth.FindNodeAsync(host.HostUrl, host.AuthEntry.IdentityUserId, ct).ConfigureAwait(false);
        if (stored is null || settings.Enroll)
        {
            if (settings.NoInteractive)
            {
                throw new CliExitException(4, "First enrollment requires browser approval. Run oscova node without --no-interactive.");
            }

            var approved = await login.LoginAsync(host, settings.Json, settings.Quiet, ct).ConfigureAwait(false);
            if (!string.Equals(approved.HostUrl, host.HostUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliExitException(4, "The login host changed. Run oscova node with the approved --host URL.");
            }

            host = new(approved.HostUrl, approved.Entry.Token, approved.Entry);
            stored = await auth.FindNodeAsync(host.HostUrl, approved.Entry.IdentityUserId, ct).ConfigureAwait(false);
            if (stored is null || settings.Enroll)
            {
                using var http = api.Create(host);
                if (stored is not null)
                {
                    using var removed = await http.DeleteAsync(ApiRoutes.Nodes.Node(stored.NodeId), ct).ConfigureAwait(false);
                    if (removed.StatusCode != HttpStatusCode.NotFound)
                    {
                        await CliHttpErrors.EnsureSuccessAsync(removed, "Revoke previous device enrollment", ct).ConfigureAwait(false);
                    }
                }

                stored = await CliRender.StatusAsync("Enrolling this device...", settings, async () =>
                {
                    using var response = await http.PostAsJsonAsync(ApiRoutes.Nodes.Root, new EnrollNodeRequest(name), CliJson.Options, ct)
                        .ConfigureAwait(false);
                    await CliHttpErrors.EnsureSuccessAsync(response, "Enroll device", ct).ConfigureAwait(false);
                    var enrollment = await response.Content.ReadFromJsonAsync<NodeEnrollment>(CliJson.Options, ct).ConfigureAwait(false)
                        ?? throw new CliExitException(1, "The server returned an empty enrollment.");
                    var entry = new CliNodeEntry(host.HostUrl, approved.Entry.IdentityUserId, enrollment.Node.Id, enrollment.Token);
                    await auth.SaveNodeAsync(entry, ct).ConfigureAwait(false);
                    return entry;
                }).ConfigureAwait(false);
            }
        }

        paths.EnsureRootDirectory();
        if (settings.Detach)
        {
            var existing = await LocalNodeControl.RequestAsync(paths, stored, false, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (settings.Json) { await CliOutput.WriteJsonAsync(existing, ct).ConfigureAwait(false); }
                else if (!settings.Quiet) { CliRender.KeyValue("Oscova node", [("Server", host.HostUrl), ("Status", existing.State)]); }

                return 0;
            }

            await LaunchDetachedAsync(host.HostUrl, settings, ct).ConfigureAwait(false);
            return 0;
        }

        await using var local = LocalNodeControl.Start(paths, stored, cancellation);
        using var logs = new NLogLoggerFactory();
        var executions = new ExecutionPermissionRegistry();
        await using var transportServices = new ServiceCollection()
            .AddHttpClient(Options.DefaultName)
            .AddHttpMessageHandler(() => new AirGapHttpHandler(new RunContextAccessor(), logs.CreateLogger<AirGapHttpHandler>()))
            .Services.BuildServiceProvider();
        await using var nodeEnvironments = new ExecutionEnvironmentCatalog(logs.CreateLogger<ExecutionEnvironmentCatalog>(), forNode: true);
        IReadOnlyList<string> privateRoots = [paths.RootDirectory, PathUtil.AppDataPath];
        IReadOnlyList<INodeCapability> capabilities =
        [
            new ProjectNodeCapability(stored.UserId, privateRoots, executions, logs, nodeEnvironments),
            new PluginNodeCapability(stored.UserId, privateRoots, logs, nodeEnvironments),
            new McpNodeCapability(stored.UserId, privateRoots, nodeEnvironments,
                transportServices.GetRequiredService<IHttpClientFactory>(), logs)
        ];
        var version = new RuntimeVersionService().GetSnapshot();
        var hello = new NodeHello(NodeProtocol.Version, version.Host.FullVersion, version.Framework.FullVersion,
            capabilities.Select(capability => capability.Description).ToArray());
        var connection = new HubConnectionBuilder()
            .WithUrl(host.HostUrl.TrimEnd('/') + NodeProtocol.HubPath, options =>
            {
                options.ApplicationMaxBufferSize = NodeProtocol.MaxTransportMessageBytes;
                options.TransportMaxBufferSize = NodeProtocol.MaxTransportMessageBytes;
                options.AccessTokenProvider = () => Task.FromResult<string?>(stored.Token);
                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler http) { http.AllowAutoRedirect = false; }

                    return handler;
                };
            })
            .WithAutomaticReconnect(new NodeReconnectPolicy())
            .Build();
        CliActivity? activity = null;
        var released = false;
        async Task ShowStatusAsync(string state, NodePolicy? policy)
        {
            local.SetState(state);
            if (startup is not null && state == "Connected" && !released)
            {
                await startup.ReleaseWhenReadyAsync(ct).ConfigureAwait(false);
                released = true;
            }

            if (activity is not null)
            {
                await activity.StopAsync().ConfigureAwait(false);
                activity = null;
            }

            if (settings.Json)
            {
                await CliOutput.WriteJsonAsync(new { state, host = host.HostUrl, nodeId = stored.NodeId,
                    permissions = policy?.GrantedPermissions }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (settings.Quiet) { return; }

            var rows = new List<(string Key, string Value)>
            {
                ("Server", host.HostUrl), ("Device ID", stored.NodeId), ("Status", state)
            };
            if (policy is not null)
            {
                rows.AddRange(UserPermissionKeys.All.Select(key => (key,
                    policy.GrantedPermissions.Contains(key, StringComparer.Ordinal) ? "Enabled" : "Disabled")));
                rows.Add(("Stop", "Press Ctrl+C. Phone access requires this node to stay connected."));
                rows.Add(("Diagnostics", PathUtil.SystemLogsPath));
            }

            CliRender.KeyValue("Oscova node", rows);
            if (state is "Connecting" or "Disconnected, reconnecting")
            {
                activity = new CliActivity(AnsiConsole.Profile.Capabilities.Interactive, state + "...");
            }
        }

        var client = new NodeClient(connection, stored.UserId, capabilities, executions, nodeEnvironments, hello,
            ShowStatusAsync, logs.CreateLogger<NodeClient>());
        try { await client.RunAsync(ct).ConfigureAwait(false); }
        finally
        {
            if (activity is not null) { await activity.StopAsync().ConfigureAwait(false); }
        }

        return 0;
    }

    /// <summary>Reuses the private startup handshake so a detached launch reports real connection readiness.</summary>
    private static async Task LaunchDetachedAsync(string host, Settings settings, CancellationToken ct)
    {
        using var pipe = CliStartupPipe.CreateServer(out var name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var arguments = new List<string> { "node", "--host", host, "--no-interactive", "--quiet", "--startup-pipe", name };
        if (settings.AllowHttp) { arguments.Add("--allow-http"); }

        using var child = await DetachedProcessLauncher.LaunchAsync(arguments, ct).ConfigureAwait(false)
            ?? throw new CliExitException(1, "Could not start the node process.");
        try
        {
            await CliRender.StatusAsync("Connecting the background node...", settings, async () =>
            {
                var connected = pipe.AcceptAsync(false, timeout.Token);
                var exited = child.WaitForExitAsync(timeout.Token);
                if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited && !connected.IsCompleted)
                {
                    await timeout.CancelAsync().ConfigureAwait(false);
                    try { await connected.ConfigureAwait(false); } catch (OperationCanceledException) { }

                    throw new CliExitException(1, $"Node startup failed. Check {PathUtil.SystemLogsPath}.");
                }

                await connected.ConfigureAwait(false);
                // Drain the child's diagnosis even when it exits before the parent reads the frame.
                var message = await pipe.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (message.Kind != StartupMessageKind.Ready) { throw new IOException("Unexpected node startup response."); }

                await pipe.SendAsync(new(StartupMessageKind.Release), timeout.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (settings.Json) { await CliOutput.WriteJsonAsync(new { state = "Connected", host, pid = child.Id }, ct).ConfigureAwait(false); }
            else if (!settings.Quiet) { CliRender.KeyValue("Oscova node", [("Server", host), ("Status", "Connected"), ("Stop", "oscova node stop")]); }
        }
        catch
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); }

            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally { await timeout.CancelAsync().ConfigureAwait(false); }

    }

    /// <summary>Reports or stops the locally enrolled node without contacting its central server.</summary>
    private async Task<int> ControlAsync(Settings settings, CancellationToken ct)
    {
        var host = await resolver.ResolveAsync(settings, requireToken: false, ct).ConfigureAwait(false);
        // Local node control uses retained enrollment even after the ordinary CLI login is removed.
        var entry = await auth.FindNodeAsync(host.HostUrl, host.AuthEntry?.IdentityUserId, ct).ConfigureAwait(false);
        var stop = settings.Action == "stop";
        var status = entry is null ? null : await CliRender.StatusAsync(stop ? "Stopping node..." : "Checking node...", settings,
            () => LocalNodeControl.RequestAsync(paths, entry, stop, ct)).ConfigureAwait(false);
        if (stop && status is not null)
        {
            try
            {
                using var process = Process.GetProcessById(status.Pid);
                if (!process.HasExited && process.StartTime.ToUniversalTime() == status.StartedAtUtc)
                {
                    await CliRender.StatusAsync("Waiting for node cleanup...", settings,
                        () => process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct)).ConfigureAwait(false);
                }
            }
            catch (ArgumentException) { }

            status = null;
        }

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new { state = status?.State ?? "Stopped", host = host.HostUrl,
                nodeId = entry?.NodeId, pid = status?.Pid }, ct).ConfigureAwait(false);
        }
        else if (!settings.Quiet)
        {
            CliRender.KeyValue("Oscova node", [("Server", host.HostUrl), ("Status", status?.State ?? "Stopped"),
                ("Device ID", entry?.NodeId ?? "Not enrolled")]);
        }

        return 0;
    }
}
