using System.ComponentModel;
using System.Diagnostics;
using NuGet.Versioning;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Core.Updates;
using Oscova.Agents.Host.Hosting;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class ApplicationUpdateSettings : OscovaCommandSettings
{
    [CommandOption("--version <VERSION>")]
    [Description("Install this version instead of the latest stable version. A lower version is allowed.")]
    public string? Version { get; init; }
}

/// <summary>
/// Updates the global tool and its plugins: stops the Host, installs the target version and restarts it detached.
/// The restarted Host takes the update checkpoint and applies plugin updates at its first start.
/// </summary>
public sealed class ApplicationUpdateCommand(
    NuGetApplicationUpdateSource source,
    LocalHostControl localControl,
    DetachedHostLauncher launcher,
    CliPaths paths) : AsyncCommand<ApplicationUpdateSettings>
{
    // The recovery retry finds no running Host to read, so it keeps the binding the first attempt read.
    private string[]? restartArguments;

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        ApplicationUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var interactive = !settings.NoInteractive && !settings.Json && !Console.IsInputRedirected &&
            AnsiConsole.Profile.Capabilities.Interactive;
        var startedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            return await UpdateAsync(settings, interactive, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CliOutput.Error(ex.Message);
            await OfferRecoveryAsync(startedAtUtc, interactive, cancellationToken).ConfigureAwait(false);
            if (interactive)
            {
                AnsiConsole.Prompt(new TextPrompt<string>("Press Enter to close.").AllowEmpty());
            }

            return ex is CliExitException exit ? exit.ExitCode : 1;
        }
    }

    private async Task<int> UpdateAsync(ApplicationUpdateSettings settings, bool interactive, CancellationToken ct)
    {
        var dotnet = ApplicationToolInstallation.ResolveDotnetPath();
        var tools = ApplicationToolInstallation.ResolveGlobalToolsPath();
        var installed = await ApplicationToolInstallation.GetInstalledVersionAsync(dotnet, ct).ConfigureAwait(false)
            ?? throw new CliExitException(1, "Oscova is not installed as a .NET global tool. Install it with 'dotnet tool install --global " +
                NuGetApplicationUpdateSource.PackageId + "'.");
        var target = await ResolveTargetAsync(settings, ct).ConfigureAwait(false);
        var workingDirectory = ResolveWorkingDirectory(tools);

        // The tool store is replaced during the update, so the update continues from a copy of the current version.
        if (PathHelper.IsPathWithinDirectory(AppContext.BaseDirectory, Path.Combine(tools, ".store")))
        {
            var updater = await CliRender.StatusAsync("Preparing the update...", settings, () =>
                Task.FromResult(ApplicationToolInstallation.PrepareUpdaterCopy(tools, installed, paths.UpdatesDirectory))).ConfigureAwait(false);
            return await HandOffAsync(dotnet, updater, target, workingDirectory, ct).ConfigureAwait(false);
        }

        return await InstallAndRestartAsync(settings, interactive, dotnet, tools, installed, target, workingDirectory, ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveTargetAsync(ApplicationUpdateSettings settings, CancellationToken ct)
    {
        if (settings.Version is not null)
        {
            if (!NuGetVersion.TryParse(settings.Version, out var requested))
            {
                throw new CliExitException(4, $"--version '{settings.Version}' is not a valid package version.");
            }

            return requested.ToNormalizedString();
        }

        return await CliRender.StatusAsync("Checking for the latest version...", settings, () => source.GetLatestVersionAsync(ct)).ConfigureAwait(false)
            ?? throw new CliExitException(1, $"No stable version was found at {NuGetApplicationUpdateSource.ResolvePackageSource()}.");
    }

    private static async Task<int> HandOffAsync(string dotnet, string updater, string target, string workingDirectory, CancellationToken ct)
    {
        string[] arguments = [updater, "update", "--version", target];
        if (OperatingSystem.IsWindows())
        {
            // Windows keeps the running store executable locked, so this process exits and the new window does the update.
            await DetachedProcessLauncher.LaunchInConsoleAsync(dotnet, arguments, workingDirectory,
                ApplicationUpdateFiles.PrepareConsoleLog(), ct).ConfigureAwait(false);
            CliOutput.Info("The update continues in a new window.");
            return 0;
        }

        var startInfo = new ProcessStartInfo { FileName = dotnet, UseShellExecute = false, WorkingDirectory = workingDirectory };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new CliExitException(1, "The update could not be started.");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    private async Task<int> InstallAndRestartAsync(
        ApplicationUpdateSettings settings,
        bool interactive,
        string dotnet,
        string tools,
        string installed,
        string target,
        string workingDirectory,
        CancellationToken ct)
    {
        var restoring = false;
        var unfinished = ApplicationUpdateFiles.FindUnfinishedFrom(target);
        if (unfinished is { ChangesBegunReached: true })
        {
            if (!interactive)
            {
                throw new CliExitException(1, $"The update from {unfinished.SourceVersion} to {unfinished.TargetVersion} changed the environment. " +
                    $"Run 'oscova update --version {target}' in a terminal to restore it.");
            }

            if (!await new ConfirmationPrompt($"Restore the environment of version {target} from its update checkpoint?")
                .ShowAsync(AnsiConsole.Console, ct).ConfigureAwait(false))
            {
                CliOutput.Info("Nothing changed.");
                return 0;
            }

            if (unfinished.State != ApplicationUpdateState.Restoring)
            {
                ApplicationUpdateFiles.Advance(unfinished, ApplicationUpdateState.Restoring);
            }

            restoring = true;
        }
        else if (unfinished is { State: not ApplicationUpdateState.Failed })
        {
            ApplicationUpdateFiles.MarkFailed(unfinished, "The update stopped before it changed the environment.");
        }

        using var updateLease = ApplicationUpdateFiles.AcquireUpdateLease();
        var lifecycleLease = await LocalLifecycleLease.AcquireAsync(paths.RootDirectory, ct).ConfigureAwait(false);
        try
        {
            var descriptor = RuntimeDescriptorWriter.ReadOrNull();
            restartArguments = descriptor?.RestartArguments is { Length: > 0 } recorded
                ? recorded
                : restartArguments ?? ["start", "--no-interactive"];
            await CliRender.StatusAsync("Stopping Oscova...", settings, () => localControl.StopLocalServicesAsync(descriptor, ct)).ConfigureAwait(false);

            if (ApplicationUpdateFiles.VersionsEqual(installed, target))
            {
                // The Host resumes an unfinished attempt to this version, except a failed plugin-only one.
                var pending = ApplicationUpdateFiles.ResolveOperation();
                var resumes = pending is { IsUnfinished: true } &&
                    ApplicationUpdateFiles.VersionsEqual(pending.TargetVersion, installed) &&
                    !(ApplicationUpdatePreparation.IsPluginOnly(pending) && pending.State == ApplicationUpdateState.Failed);
                if (!restoring && !resumes)
                {
                    ApplicationUpdateFiles.StartOperation(installed, installed, EnvironmentDataFormat.CurrentVersion);
                }
            }
            else
            {
                try
                {
                    await InstallAsync(settings, dotnet, installed, target, workingDirectory, ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    lifecycleLease.Dispose();
                    CliOutput.Error($"Version {target} could not be installed. Restarting version {installed}.");
                    await RestartAsync(tools, installed, restartArguments, workingDirectory, ct).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            lifecycleLease.Dispose();
        }

        await RestartAsync(tools, target, restartArguments, workingDirectory, ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task InstallAsync(
        ApplicationUpdateSettings settings,
        string dotnet,
        string installed,
        string target,
        string workingDirectory,
        CancellationToken ct)
    {
        List<string> arguments =
        [
            "tool", "update", NuGetApplicationUpdateSource.PackageId, "--global",
            "--version", target, "--source", NuGetApplicationUpdateSource.ResolvePackageSource()
        ];
        if (NuGetVersion.Parse(target) < NuGetVersion.Parse(installed))
        {
            arguments.Add("--allow-downgrade");
        }

        // Ctrl+C or closing the update console signals every process in it. The SDK must still finish replacing the
        // tool store, because a replacement stopped midway leaves no version installed. On Unix the SDK ignores both
        // signals (an ignored signal stays ignored across exec). Windows already gives it its own hidden console.
        var output = await CliRender.StatusAsync($"Installing Oscova {target}...", settings, () => OperatingSystem.IsWindows()
            ? ApplicationToolInstallation.RunDotnetAsync(dotnet, arguments, ct, workingDirectory)
            : ApplicationToolInstallation.RunDotnetAsync("/bin/sh",
                ["-c", "trap '' INT HUP; exec \"$0\" \"$@\"", dotnet, .. arguments], ct, workingDirectory)).ConfigureAwait(false);
        CliOutput.Info(output.Trim(), settings.Quiet);
        var current = await ApplicationToolInstallation.GetInstalledVersionAsync(dotnet, ct).ConfigureAwait(false);
        if (current is null || !ApplicationUpdateFiles.VersionsEqual(current, target))
        {
            throw new InvalidOperationException($"The .NET SDK did not install Oscova {target}.");
        }
    }

    private async Task RestartAsync(string tools, string version, IReadOnlyList<string> restartArguments, string workingDirectory, CancellationToken ct)
    {
        CliOutput.Info($"Starting Oscova {version}...");
        var descriptor = await launcher.LaunchAsync(
            ApplicationToolInstallation.ResolveInstalledCommand(tools, version), restartArguments, workingDirectory, ct).ConfigureAwait(false);
        CliRender.Success($"Oscova {version} is running at {descriptor.Urls.FirstOrDefault() ?? "unknown URL"} (pid {descriptor.Pid}).");
    }

    // Shows what the failed attempt left and offers the way back to the version it started from.
    private async Task OfferRecoveryAsync(DateTimeOffset startedAtUtc, bool interactive, CancellationToken ct)
    {
        // Older failed attempts are history. Only an attempt this run touched has a way back to offer.
        if (ApplicationUpdateFiles.ResolveOperation() is not { IsUnfinished: true } operation || operation.UpdatedAtUtc < startedAtUtc)
        {
            return;
        }

        var directory = ApplicationUpdateFiles.GetOperationDirectory(operation.OperationId);
        CliRender.KeyValue("Unfinished update",
        [
            ("Versions", CliRender.Value($"{operation.SourceVersion} -> {operation.TargetVersion}")),
            ("Stopped at", CliRender.Status((operation.FailedPhase ?? operation.State).ToString())),
            ("Backup", CliRender.Value(Path.Combine(directory, ApplicationUpdateFiles.BackupFileName))),
            ("Recovery key", CliRender.Value(Path.Combine(directory, ApplicationUpdateFiles.RecoveryKeyFileName))),
            ("Log", CliRender.Value(Path.Combine(directory, ApplicationUpdateFiles.LogFileName)))
        ]);
        // A plugin-only attempt that changed nothing has no other version to go back to.
        if (ApplicationUpdatePreparation.IsPluginOnly(operation) && !operation.ChangesBegunReached)
        {
            return;
        }

        if (!interactive)
        {
            CliOutput.Info($"Run 'oscova update --version {operation.SourceVersion}' to go back to version {operation.SourceVersion}.");
            return;
        }

        try
        {
            // A changed environment gets its own restore question from the update itself.
            if (operation.ChangesBegunReached ||
                await new ConfirmationPrompt($"Go back to version {operation.SourceVersion}?").ShowAsync(AnsiConsole.Console, ct).ConfigureAwait(false))
            {
                var retry = new ApplicationUpdateSettings { Version = operation.SourceVersion };
                await UpdateAsync(retry, interactive, ct).ConfigureAwait(false);
                return;
            }

            if (await new ConfirmationPrompt("Open the update folder?") { DefaultValue = false }.ShowAsync(AnsiConsole.Console, ct).ConfigureAwait(false))
            {
                OpenFolder(directory);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CliOutput.Error(ex.Message);
            CliOutput.Info($"Follow {Path.Combine(directory, ApplicationUpdateFiles.RecoveryInstructionsFileName)} to recover by hand.");
        }
    }

    private static void OpenFolder(string directory)
    {
        var opener = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        using var process = Process.Start(new ProcessStartInfo { FileName = opener, ArgumentList = { directory }, UseShellExecute = false });
    }

    private static string ResolveWorkingDirectory(string tools)
    {
        var current = Path.GetFullPath(Environment.CurrentDirectory);
        return PathHelper.IsPathWithinDirectory(current, Path.Combine(tools, ".store"))
            ? Path.GetFullPath(PathUtil.AppDataPath)
            : current;
    }
}

/// <summary>Shows retained application update and recovery evidence.</summary>
public sealed class ApplicationUpdateStatusCommand : AsyncCommand<OscovaCommandSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        OscovaCommandSettings settings,
        CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var operation = ApplicationUpdateFiles.ResolveOperation();
        if (operation is null)
        {
            const string message = "No application update operation is available.";
            // The absent operation is still a JSON result.
            if (settings.Json)
            {
                await CliOutput.WriteJsonAsync<object?>(null, cancellationToken).ConfigureAwait(false);
            }
            else if (!settings.Quiet &&
                AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Profile.Capabilities.Ansi)
            {
                AnsiConsole.Write(CliRender.Card(
                    "Application update",
                    new Text(message, CliTheme.Label),
                    CliTheme.AccentDim));
            }
            else
            {
                CliOutput.Info(message, settings.Quiet);
            }

            return 0;
        }

        var directory = ApplicationUpdateFiles.GetOperationDirectory(operation.OperationId);
        var consoleLogPath = Path.Combine(ApplicationUpdateFiles.UpdatesDirectory, ApplicationUpdateFiles.ConsoleLogFileName);
        var result = new
        {
            operation.OperationId,
            operation.SourceVersion,
            operation.TargetVersion,
            State = operation.State.ToString(),
            FailedPhase = operation.FailedPhase?.ToString(),
            operation.Diagnostic,
            operation.PluginOutcomes,
            BackupPath = Path.Combine(directory, ApplicationUpdateFiles.BackupFileName),
            RecoveryKeyPath = Path.Combine(directory, ApplicationUpdateFiles.RecoveryKeyFileName),
            RecoveryInstructionsPath = Path.Combine(directory, ApplicationUpdateFiles.RecoveryInstructionsFileName),
            LogPath = Path.Combine(directory, ApplicationUpdateFiles.LogFileName),
            ConsoleLogPath = File.Exists(consoleLogPath) ? consoleLogPath : null
        };
        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(result, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            CliRender.KeyValue("Application update",
            [
                ("Operation", CliRender.Identifier(result.OperationId)),
                ("Versions", CliRender.Value($"{result.SourceVersion} -> {result.TargetVersion}")),
                ("State", CliRender.Status(result.FailedPhase is null ? result.State : $"{result.State} at {result.FailedPhase}")),
                ("Backup", CliRender.Value(result.BackupPath)),
                ("Recovery key", CliRender.Value(result.RecoveryKeyPath)),
                ("Instructions", CliRender.Value(result.RecoveryInstructionsPath)),
                ("Log", CliRender.Value(result.LogPath)),
                ("Console log", CliRender.Value(result.ConsoleLogPath ?? "None")),
                ("Diagnostic", CliRender.Value(result.Diagnostic ?? "None"))
            ]);
            if (operation.PluginOutcomes.Count > 0)
            {
                var table = new Table().AddColumn("Plugin").AddColumn("Result").AddColumn("Details");
                foreach (var outcome in operation.PluginOutcomes)
                {
                    table.AddRow(Markup.Escape(outcome.Name), Markup.Escape(outcome.State),
                        Markup.Escape(outcome.Reason ?? outcome.Version ?? string.Empty));
                }

                AnsiConsole.Write(new Panel(table).Header("Plugin files and settings kept"));
            }
        }

        return 0;
    }
}
