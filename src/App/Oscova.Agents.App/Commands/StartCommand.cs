using System.ComponentModel;
using System.Diagnostics;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Host;
using Oscova.Agents.Host.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Infrastructure.Backup;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Infrastructure.Updates;

namespace Oscova.Agents.App.Commands;

public sealed class StartCommand(
    StartCollisionChecker collisionChecker,
    DetachedHostLauncher launcher,
    RemoteHostStartPrompt remoteHostPrompt,
    OriginalCommandLineArgs originalArgs,
    AvatarEngineManager avatarEngineManager,
    ApplicationToolInstallation toolInstallation,
    CliPaths paths) : AsyncCommand<StartCommand.Settings>
{
    public sealed class Settings : OscovaCommandSettings
    {
        [CommandOption("--detach")]
        [Description("Show startup progress, then leave the ready host running in the background.")]
        public bool Detach { get; init; }

        [CommandOption("--port <PORT>")]
        [Description("TCP port to bind when starting locally. Defaults to the configured port.")]
        public int? Port { get; init; }

        [CommandOption("--host-bind <HOST>")]
        [Description("Network interface to bind when starting locally.")]
        public string? HostBind { get; init; }

        [CommandOption("--wait-for-pid <PID>")]
        [Description("Wait for an existing host process to exit before starting.")]
        public int? WaitForPid { get; init; }

        [CommandOption("--startup-pipe <NAME>", IsHidden = true)]
        [Description("Connect to the terminal that owns this detached startup.")]
        public string? StartupPipe { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        using var pipe = settings.StartupPipe is null ? null : await CliStartupPipe.ConnectChildAsync(settings.StartupPipe, cancellationToken).ConfigureAwait(false);
        // A non-interactive child needs no answers from its terminal, so closing that terminal leaves it starting.
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, pipe?.Interactive == true ? pipe.ParentDisconnected : CancellationToken.None);
        cancellationToken = startupCancellation.Token;
        if (settings.WaitForPid is int waitForPid)
        {
            await WaitForProcessExitAsync(waitForPid, cancellationToken).ConfigureAwait(false);
        }

        var runningHost = await collisionChecker.FindRunningHostAsync(cancellationToken).ConfigureAwait(false);
        if (runningHost is not null)
        {
            await RenderRuntimeAsync(runningHost, settings, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (remoteHostPrompt.ShouldUseRemote(settings) && !RemoteHostStartPrompt.ConfirmStartLocal(settings))
        {
            CliOutput.Info("Using configured remote host.", settings.Quiet);
            return 0;
        }

        if (settings.Detach)
        {
            var startupMessages = new List<string>();
            var descriptor = await launcher.LaunchAsync(settings, cancellationToken, startupMessages.Add).ConfigureAwait(false);
            await RenderRuntimeAsync(descriptor, settings, cancellationToken, startupMessages).ConfigureAwait(false);
            return 0;
        }

        paths.ThrowIfPackageLocalEnvironmentNeedsTransfer();
        var pendingOperation = EnvironmentRestoreBootstrapper.ReadPendingOperationOrNull();
        var restartArgs = pendingOperation is { Mode: EnvironmentOperationMode.Reset, ResetRestartArguments: not null }
            ? pendingOperation.ResetRestartArguments.ToArray()
            : originalArgs.Args;
        var toolIdentity = await toolInstallation.TryResolveCurrentAsync(restartArgs, cancellationToken).ConfigureAwait(false);

        var showActivity = !settings.Json && !settings.Quiet && !settings.NoInteractive &&
            AnsiConsole.Profile.Capabilities.Interactive &&
            AnsiConsole.Profile.Capabilities.Ansi;
        CliActivity BeginStartupActivity() => new(showActivity, "Starting Oscova...");
        var activity = BeginStartupActivity();
        try
        {
            var options = new WebHostRunOptions
            {
                BindingOverride = settings.Port is null && string.IsNullOrWhiteSpace(settings.HostBind)
                    ? null
                    : new NetworkBindingOverride(settings.HostBind, settings.Port),
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                OriginalCommandLineArgs = restartArgs,
                ToolInstallation = toolIdentity,
                AvatarEngineOperations = avatarEngineManager,
                StartupCancellationToken = cancellationToken,
                StartupVersionChanged = message =>
                {
                    if (pipe is not null)
                    {
                        pipe.SendAsync(new(StartupMessageKind.Version, message), cancellationToken).GetAwaiter().GetResult();
                        return;
                    }

                    activity.StopAsync().GetAwaiter().GetResult();
                    if (!settings.Json) { CliOutput.Info(message, settings.Quiet); }

                    activity = BeginStartupActivity();
                },
                StartConsole = toolIdentity is null
                    ? null
                    : async (targetVersion, token) =>
                    {
                        // The update runs from a copy of the current version, so it survives replacing the tool store.
                        var updater = ApplicationToolInstallation.PrepareUpdaterCopy(
                            toolIdentity.GlobalToolsPath, toolIdentity.PackageVersion, toolIdentity.UpdatesDirectory);
                        return await DetachedProcessLauncher.LaunchInConsoleAsync(
                            toolIdentity.DotnetPath,
                            [updater, "update", "--version", targetVersion],
                            toolIdentity.WorkingDirectory,
                            ApplicationUpdateFiles.PrepareConsoleLog(),
                            token).ConfigureAwait(false);
                    },
                StartupComponentInstallation = async install =>
                {
                    if (pipe is not null)
                    {
                        await pipe.SendAsync(new(StartupMessageKind.ComponentsBegin), cancellationToken).ConfigureAwait(false);
                        await install(progress => pipe.SendAsync(new(StartupMessageKind.Component, Component: progress), cancellationToken).GetAwaiter().GetResult()).ConfigureAwait(false);
                        await pipe.SendAsync(new(StartupMessageKind.ComponentsEnd), cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await activity.StopAsync().ConfigureAwait(false);
                    await CliComponentProgress.RunAsync(install, settings).ConfigureAwait(false);
                    activity = BeginStartupActivity();
                },
                StartupPluginSelection = (pipe?.Interactive ?? ShouldOfferAvatarEngine(settings))
                    ? async (offered, token) =>
                    {
                        await activity.StopAsync().ConfigureAwait(false);
                        var selected = pipe is not null
                            ? await pipe.RequestPluginSelectionAsync(offered, token).ConfigureAwait(false)
                            : await CliStartupPlugins.SelectAsync(AnsiConsole.Console, offered, token).ConfigureAwait(false);
                        activity = BeginStartupActivity();
                        return selected;
                    } : null,
                StartupNotice = message =>
                {
                    if (pipe is not null) { pipe.SendAsync(new(StartupMessageKind.Notice, message), cancellationToken).GetAwaiter().GetResult(); }
                    else if (!settings.Json)
                    {
                        activity.StopAsync().GetAwaiter().GetResult();
                        CliOutput.Info(message, settings.Quiet);
                        activity = BeginStartupActivity();
                    }
                },
                StartupPluginResults = reasons =>
                {
                    if (reasons.Count == 0) { return; }

                    var text = string.Join(Environment.NewLine, reasons.Select(item => $"{item.Key}: {item.Value}")) + Environment.NewLine + "Plugin files and settings have been kept.";
                    if (pipe is not null) { pipe.SendAsync(new(StartupMessageKind.PluginResults, text), cancellationToken).GetAwaiter().GetResult(); }
                    else if (!settings.Json)
                    {
                        activity.StopAsync().GetAwaiter().GetResult();
                        var table = new Table().AddColumn("Disabled plugin").AddColumn("Reason");
                        foreach (var item in reasons) { table.AddRow(Markup.Escape(item.Key), Markup.Escape(item.Value)); }

                        AnsiConsole.Write(new Panel(table).Header("Plugin files and settings kept"));
                        activity = BeginStartupActivity();
                    }
                },
                StartupComponentsReady = async token =>
                {
                    if (pipe?.Interactive ?? ShouldOfferAvatarEngine(settings))
                    {
                        await activity.StopAsync().ConfigureAwait(false);
                        await OfferAvatarEngineAsync(settings, pipe, token).ConfigureAwait(false);
                        activity = BeginStartupActivity();
                    }

                    if (pipe is not null) { await pipe.SendAsync(new(StartupMessageKind.Prepared), token).ConfigureAwait(false); }
                },
                StartupReady = pipe is not null
                    ? _ => pipe.ReleaseWhenReadyAsync(cancellationToken).GetAwaiter().GetResult()
                    : settings.Json
                    ? null
                    : urls =>
                    {
                        // The Host callback is synchronous, join only the released display.
                        activity.StopAsync().GetAwaiter().GetResult();
                        RenderRuntime(Environment.ProcessId, urls, settings);
                    }
            };
            await WebHostStartup.RunAsync(originalArgs.Args, options).ConfigureAwait(false);
        }
        catch (Exception exception) when (pipe is not null || exception is DatabaseStartupException)
        {
            var message = CliExceptionRenderer.FormatStartupFailure(exception);
            if (pipe is not null)
            {
                try
                {
                    await pipe.SendAsync(new(StartupMessageKind.Failed, message), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception) { /* A disconnected parent already owns child cleanup. */ }
            }

            if (exception is DatabaseStartupException) { throw new CliExitException(1, message); }

            throw;
        }
        finally
        {
            await activity.StopAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static bool ShouldOfferAvatarEngine(Settings settings) =>
        !settings.Detach &&
        !settings.NoInteractive &&
        !settings.Quiet &&
        !settings.Json &&
        !Console.IsInputRedirected &&
        AnsiConsole.Profile.Capabilities.Interactive;

    private async Task OfferAvatarEngineAsync(Settings settings, CliStartupPipe? pipe, CancellationToken cancellationToken)
    {
        if (Directory.Exists(paths.AvatarEngineDirectory))
        {
            if (!(await avatarEngineManager.GetStatusAsync(cancellationToken).ConfigureAwait(false)).Installed)
            {
                await NoticeAsync("Avatar Engine installation is incomplete. Repair it with 'oscova avatar install'.").ConfigureAwait(false);
            }

            return;
        }

        if (File.Exists(paths.AvatarOnboardingMarkerFilePath))
        {
            return;
        }

        try
        {
            CliFileSecurity.EnsurePrivateDirectory(paths.AvatarDirectory);
            if (!OperatingSystem.IsWindows())
            {
                if (pipe is not null) { await pipe.SendAsync(new(StartupMessageKind.AvatarPlatformNotice), cancellationToken).ConfigureAwait(false); }
                else { CliRender.AvatarPlatformNotice(); }

                await WriteAvatarOnboardingMarkerAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (pipe is null) { CliRender.AvatarInstallNotice(); }

            var install = pipe is not null ? await pipe.RequestAvatarChoiceAsync(cancellationToken).ConfigureAwait(false) : AnsiConsole.Prompt(new ConfirmationPrompt(
                "Install Avatar Engine on this computer now?")
            {
                DefaultValue = false
            });

            if (!install)
            {
                await WriteAvatarOnboardingMarkerAsync(cancellationToken).ConfigureAwait(false);
                await NoticeAsync("Install later: oscova avatar install").ConfigureAwait(false);
                return;
            }

            if (pipe is null)
            {
                await CliRender.RunAvatarInstallAsync(
                    (progress, token) => avatarEngineManager.InstallUnderStartupLeaseAsync(null, token, progress),
                    settings, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await pipe.SendAsync(new(StartupMessageKind.AvatarBegin), cancellationToken).ConfigureAwait(false);
                try
                {
                    await avatarEngineManager.InstallUnderStartupLeaseAsync(null, cancellationToken,
                        progress => pipe.SendAsync(new(StartupMessageKind.AvatarProgress, Avatar: progress), cancellationToken).GetAwaiter().GetResult()).ConfigureAwait(false);
                }
                finally { await pipe.SendAsync(new(StartupMessageKind.AvatarEnd), cancellationToken).ConfigureAwait(false); }
            }

            await NoticeAsync("Avatar Engine installed. Start it when ready with 'oscova avatar start'.").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await NoticeAsync(pipe is null ? $"Avatar Engine installation failed: {exception.Message}. Retry with 'oscova avatar install'." :
                "Avatar Engine installation failed. Retry with 'oscova avatar install'.").ConfigureAwait(false);
        }

        Task NoticeAsync(string message) => pipe is not null
            ? pipe.SendAsync(new(StartupMessageKind.Notice, message), cancellationToken)
            : WriteNoticeAsync(message);
        static Task WriteNoticeAsync(string message) { CliOutput.Info(message); return Task.CompletedTask; }
    }

    private async Task WriteAvatarOnboardingMarkerAsync(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(paths.AvatarOnboardingMarkerFilePath, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RenderRuntimeAsync(RuntimeDescriptor descriptor, Settings settings, CancellationToken cancellationToken, IReadOnlyList<string>? startupMessages = null)
    {
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(
                new RuntimeDescriptorOutput(descriptor.Pid, descriptor.Urls, descriptor.StartedAtUtc, startupMessages ?? []),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        RenderRuntime(descriptor.Pid, descriptor.Urls, settings);
    }

    private static void RenderRuntime(int pid, IReadOnlyList<string> urls, Settings settings)
    {
        // A wildcard bind such as 0.0.0.0 is not an address a browser can open.
        var url = CliHostUrlSelector.SelectLocalHost(urls);
        var message = $"Oscova is running at {url} (pid {pid}).";
        var showCard = !settings.Quiet && !settings.NoInteractive &&
            AnsiConsole.Profile.Capabilities.Interactive &&
            AnsiConsole.Profile.Capabilities.Ansi;
        if (showCard)
        {
            CliRender.KeyValue("Oscova is ready",
            [
                ("Address", url),
                ("Process", pid.ToString()),
                ("Environment", PathUtil.AppDataPath),
                ("Next", "Open this address to continue setup or sign in.")
            ]);
        }
        else
        {
            CliOutput.Info(message, settings.Quiet);
            CliOutput.Info($"Environment: {PathUtil.AppDataPath}. Open this address to continue setup or sign in.", settings.Quiet);
        }
    }

    private sealed record RuntimeDescriptorOutput(int Pid, string[] Urls, DateTimeOffset StartedAtUtc, IReadOnlyList<string> StartupMessages);

    private static async Task WaitForProcessExitAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            while (!process.HasExited)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                process.Refresh();
            }
        }
        catch (ArgumentException)
        {
        }
    }
}
