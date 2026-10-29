using Oscova.Agents.Host.Hosting;
using Oscova.Agents.Host.Server.Hosting;
using Oscova.Agents.Infrastructure.Utils;
using Spectre.Console;
using System.Diagnostics;

namespace Oscova.Agents.App.Cli;

/// <summary>Starts a detached Host and keeps preparation visible until its readiness is verified.</summary>
public sealed class DetachedHostLauncher(
    OriginalCommandLineArgs originalArgs,
    StartCollisionChecker collisionChecker,
    CliPaths paths)
{
    /// <summary>Replays startup with the original parent's interaction settings.</summary>
    public Task<RuntimeDescriptor> LaunchAsync(OscovaCommandSettings settings, CancellationToken ct, Action<string>? reportStartup = null) =>
        LaunchCoreAsync(BuildReplayArgs(originalArgs.Args), settings, args => DetachedProcessLauncher.LaunchAsync(args, ct), ct, reportStartup);

    /// <summary>Launches a Gateway-selected local Host using the shared startup relay.</summary>
    public Task<RuntimeDescriptor> LaunchAsync(IReadOnlyList<string> arguments, OscovaCommandSettings settings, CancellationToken ct) =>
        LaunchCoreAsync(arguments, settings, args => DetachedProcessLauncher.LaunchAsync(args, ct), ct);

    /// <summary>Launches an installed update target using the same preparation and readiness boundaries.</summary>
    public Task<RuntimeDescriptor> LaunchAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct) =>
        LaunchCoreAsync(arguments, new OscovaCommandSettings { NoInteractive = true },
            args => DetachedProcessLauncher.LaunchAsync(executable, args, workingDirectory, ct), ct);

    private async Task<RuntimeDescriptor> LaunchCoreAsync(
        IReadOnlyList<string> arguments,
        OscovaCommandSettings settings,
        Func<IReadOnlyList<string>, Task<Process?>> launch,
        CancellationToken ct,
        Action<string>? reportStartup = null)
    {
        paths.ThrowIfPackageLocalEnvironmentNeedsTransfer();
        using var pipe = CliStartupPipe.CreateServer(out var name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var child = await launch(arguments.Concat(["--startup-pipe", name]).ToArray()).ConfigureAwait(false)
            ?? throw new CliExitException(1, "Could not identify the detached Oscova process.");
        var exited = child.WaitForExitAsync(timeout.Token);
        Task<RuntimeDescriptor>? relay = null;
        try
        {
            var interactive = !settings.Json && !settings.Quiet && !settings.NoInteractive &&
                !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive;
            var connect = pipe.AcceptAsync(interactive, timeout.Token);
            await CliRender.StatusAsync("Connecting to Oscova startup...", settings, async () =>
            {
                if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == exited) { throw ChildExited(); }

                await connect.ConfigureAwait(false);
            }).ConfigureAwait(false);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            relay = RelayAsync(pipe, child, settings, timeout, reportStartup);
            if (await Task.WhenAny(relay, exited).ConfigureAwait(false) == exited) { throw ChildExited(); }

            return await relay.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            }

            if (ex is OperationCanceledException && timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new CliExitException(1, $"Oscova did not finish its startup connection or report readiness in time. Check {PathUtil.SystemLogsPath}.");
            }

            if (ex is EndOfStreamException) { throw ChildExited(); }

            throw;
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            if (relay is not null)
            {
                try { await relay.ConfigureAwait(false); }
                catch (Exception) { /* The caller receives the original startup failure. */ }
            }
        }
    }

    private async Task<RuntimeDescriptor> RelayAsync(CliStartupPipe pipe, Process child, OscovaCommandSettings settings, CancellationTokenSource timeout, Action<string>? reportStartup)
    {
        var ct = timeout.Token;
        var componentsStarted = false;
        var componentsFinished = false;
        var avatarRequested = false;
        var avatarStarted = false;
        var prepared = false;
        var showActivity = !settings.Json && !settings.Quiet && !settings.NoInteractive &&
            AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi;
        while (true)
        {
            var activity = new CliActivity(showActivity, prepared ? "Waiting for Oscova readiness..." : "Preparing Oscova...");
            StartupMessage message;
            try { message = await pipe.ReadAsync(ct).ConfigureAwait(false); }
            finally { await activity.StopAsync().ConfigureAwait(false); }

            switch (message.Kind)
            {
                case StartupMessageKind.Version when !componentsStarted:
                case StartupMessageKind.Notice when !prepared:
                    reportStartup?.Invoke(message.Text ?? string.Empty);
                    if (!settings.Json) { CliOutput.Info(message.Text ?? string.Empty, settings.Quiet); }

                    break;
                case StartupMessageKind.PluginSelection when !prepared && pipe.Interactive:
                    var offered = message.OfferedPlugins ?? throw UnexpectedMessage();
                    var selected = await CliStartupPlugins.SelectAsync(AnsiConsole.Console, offered, ct).ConfigureAwait(false);
                    await pipe.SendAsync(new(StartupMessageKind.PluginSelectionAnswer, SelectedPlugins: selected), ct).ConfigureAwait(false);
                    break;
                case StartupMessageKind.AvatarPlatformNotice when componentsFinished && !prepared:
                    if (!settings.Json && !settings.Quiet) { CliRender.AvatarPlatformNotice(); }

                    break;
                case StartupMessageKind.PluginResults when !prepared:
                    reportStartup?.Invoke(message.Text ?? string.Empty);
                    if (!settings.Json) { AnsiConsole.Write(new Panel(new Markup(Markup.Escape(message.Text ?? string.Empty))).Header("Plugin compatibility")); }

                    break;
                case StartupMessageKind.ComponentsBegin when !prepared:
                    componentsStarted = true;
                    await CliComponentProgress.RunAsync(async report =>
                    {
                        while (true)
                        {
                            var progress = await pipe.ReadAsync(ct).ConfigureAwait(false);
                            if (progress.Kind == StartupMessageKind.ComponentsEnd) { break; }

                            if (progress.Kind != StartupMessageKind.Component || progress.Component is null) { throw UnexpectedMessage(); }

                            report?.Invoke(progress.Component);
                        }
                    }, settings).ConfigureAwait(false);
                    componentsFinished = true;
                    break;
                case StartupMessageKind.AvatarChoice when componentsFinished && !avatarRequested && !prepared:
                    avatarRequested = true;
                    if (!settings.Json && !settings.Quiet) { CliRender.AvatarInstallNotice(); }

                    var install = !settings.Json && !settings.Quiet && !settings.NoInteractive && !Console.IsInputRedirected &&
                        AnsiConsole.Profile.Capabilities.Interactive && await new ConfirmationPrompt(
                            "Install Avatar Engine on this computer now?") { DefaultValue = false }
                            .ShowAsync(AnsiConsole.Console, ct).ConfigureAwait(false);
                    await pipe.SendAsync(new(StartupMessageKind.AvatarAnswer, Choice: install), ct).ConfigureAwait(false);
                    break;
                case StartupMessageKind.AvatarBegin when avatarRequested && !avatarStarted && !prepared:
                    avatarStarted = true;
                    await CliRender.RunAvatarInstallAsync(async (report, token) =>
                    {
                        while (true)
                        {
                            var progress = await pipe.ReadAsync(token).ConfigureAwait(false);
                            if (progress.Kind == StartupMessageKind.AvatarEnd) { break; }

                            if (progress.Kind != StartupMessageKind.AvatarProgress || progress.Avatar is null) { throw UnexpectedMessage(); }

                            report?.Invoke(progress.Avatar);
                        }
                    }, settings, ct).ConfigureAwait(false);
                    break;
                case StartupMessageKind.Prepared when !prepared:
                    prepared = true;
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    break;
                case StartupMessageKind.Ready when prepared:
                    var descriptor = await collisionChecker.FindRunningHostAsync(ct).ConfigureAwait(false);
                    if (descriptor is null || descriptor.Pid != child.Id || descriptor.ProcessStartIdentifier == 0 ||
                        descriptor.ProcessStartIdentifier != ProcessIdentity.GetStartIdentifier(child))
                    {
                        throw new CliExitException(1, "The detached Host did not publish its own readiness descriptor.");
                    }

                    await pipe.SendAsync(new(StartupMessageKind.Release), ct).ConfigureAwait(false);
                    return descriptor;
                default:
                    throw UnexpectedMessage();
            }
        }
    }

    /// <summary>Removes launch-only arguments and prevents the detached child from prompting.</summary>
    public static IReadOnlyList<string> BuildReplayArgs(string[] originalArgs) => HostProcessRelauncher.NormalizeRestartArgs(originalArgs);

    private static CliExitException ChildExited() => new(1, $"Oscova exited during startup. Check {PathUtil.SystemLogsPath}.");
    private static IOException UnexpectedMessage() => new("Unexpected detached startup message.");
}
