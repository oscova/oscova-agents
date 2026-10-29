using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Core.ApiContracts.Updates;
using Oscova.Agents.Core.Updates;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.App.Cli;

/// <summary>Installs and controls the optional Windows Avatar Engine independently from the Oscova Host.</summary>
public sealed class AvatarEngineManager(CliPaths paths, AvatarEngineDownload download) : IAvatarEngineOperations
{
    private const string ExecutableName = "OscovaAvatarEngine.exe";
    private const string ReceiptName = "oscova-release.json";
    private string RecoveryDirectory => Path.Combine(paths.AvatarDirectory, ".engine-recovery");

    /// <summary>Installs a supplied local ZIP or downloads the published Avatar Engine.</summary>
    public async Task<bool> InstallAsync(
        string? sourcePath,
        CancellationToken cancellationToken,
        Action<AvatarDownloadProgress>? progress = null,
        bool replace = false)
    {
        EnsureWindows();
        using var lease = await LocalLifecycleLease.AcquireAsync(paths.RootDirectory, cancellationToken).ConfigureAwait(false);
        return await InstallUnderStartupLeaseAsync(sourcePath, cancellationToken, progress, replace).ConfigureAwait(false);
    }

    /// <summary>Installs during Host onboarding while the same process already owns startup coordination.</summary>
    internal async Task<bool> InstallUnderStartupLeaseAsync(
        string? sourcePath,
        CancellationToken cancellationToken,
        Action<AvatarDownloadProgress>? progress = null,
        bool replace = false,
        AvatarReleaseIdentity? accepted = null)
    {
        EnsureWindows();
        if (sourcePath is not null &&
            Uri.TryCreate(sourcePath, UriKind.Absolute, out var sourceUri) &&
            sourceUri.Scheme is "http" or "https")
        {
            throw new CliExitException(4, "Avatar Engine --source accepts a local ZIP only.");
        }

        if (sourcePath is not null && (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)))
        {
            throw new CliExitException(4, $"No Avatar Engine ZIP at '{sourcePath}'.");
        }

        RecoverInterruptedReplacement();
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Running)
        {
            throw new CliExitException(1, "Stop the Avatar Engine with 'oscova avatar stop' before installing it.");
        }

        AvatarReleaseIdentity? expected = accepted;
        if (Directory.Exists(paths.AvatarEngineDirectory))
        {
            var check = await CheckInstallationAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (expected is not null && check.Published is not null && !expected.MatchesContent(check.Published))
            {
                throw new CliExitException(1, "The Avatar Engine release changed after confirmation. Review its current release again.");
            }

            if (check.State == "current") { return false; }

            if (!replace) { throw new CliExitException(4, "Avatar Engine replacement requires confirmation. Run 'oscova avatar install --yes' to replace it."); }

            expected ??= check.Published;
        }

        CliFileSecurity.EnsurePrivateDirectory(paths.AvatarDirectory);
        string? downloadDirectory = null;
        var temporaryDirectory = Path.Combine(paths.AvatarDirectory, ".engine-" + Guid.NewGuid().ToString("N"));

        try
        {
            AvatarReleaseIdentity receipt;
            if (sourcePath is null)
            {
                downloadDirectory = Path.Combine(paths.AvatarDirectory, ".download-" + Guid.NewGuid().ToString("N"));
                CliFileSecurity.EnsurePrivateDirectory(downloadDirectory);
                sourcePath = Path.Combine(downloadDirectory, "avatar-engine.zip");
                receipt = await download.DownloadAsync(sourcePath, progress, cancellationToken).ConfigureAwait(false);
            }
            else { receipt = await ReadLocalIdentityAsync(sourcePath, cancellationToken).ConfigureAwait(false); }

            if (expected?.MatchesContent(receipt) == false)
            {
                throw new CliExitException(1, "The Avatar Engine release changed after confirmation. Run the installation again.");
            }

            progress?.Invoke(new(receipt.SizeBytes, receipt.SizeBytes, Message: "Installing"));
            CheckExpandedFreeSpace(sourcePath);

            try
            {
                await ZipFile.ExtractToDirectoryAsync(
                    sourcePath,
                    temporaryDirectory,
                    overwriteFiles: false,
                    cancellationToken).ConfigureAwait(false);

                if (!File.Exists(Path.Combine(temporaryDirectory, ExecutableName)))
                {
                    throw new InvalidDataException($"Avatar Engine ZIP must contain '{ExecutableName}' at its root.");
                }

                await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, ReceiptName), JsonSerializer.Serialize(receipt, CliJson.Options), cancellationToken).ConfigureAwait(false);
                // Publish receipt and files together, retaining the old directory until promotion succeeds.
                if (Directory.Exists(paths.AvatarEngineDirectory))
                {
                    await MoveDirectoryAsync(paths.AvatarEngineDirectory, RecoveryDirectory, cancellationToken).ConfigureAwait(false);
                }

                try { await MoveDirectoryAsync(temporaryDirectory, paths.AvatarEngineDirectory, cancellationToken).ConfigureAwait(false); }
                catch
                {
                    if (!Directory.Exists(paths.AvatarEngineDirectory) && Directory.Exists(RecoveryDirectory))
                    {
                        await MoveDirectoryAsync(RecoveryDirectory, paths.AvatarEngineDirectory, CancellationToken.None).ConfigureAwait(false);
                    }

                    throw;
                }

                DeleteTemporaryDirectory(RecoveryDirectory);
            }
            catch
            {
                DeleteTemporaryDirectory(temporaryDirectory);
                throw;
            }
        }
        finally
        {
            if (downloadDirectory is not null)
            {
                DeleteTemporaryDirectory(downloadDirectory);
            }
        }

        return true;
    }

    /// <summary>Stops and removes the locally installed Avatar Engine.</summary>
    public async Task UninstallAsync(CancellationToken cancellationToken)
    {
        EnsureWindows();
        using var lease = await LocalLifecycleLease.AcquireAsync(paths.RootDirectory, cancellationToken).ConfigureAwait(false);
        RecoverInterruptedReplacement();
        await StopAsync(cancellationToken).ConfigureAwait(false);

        if (Directory.Exists(paths.AvatarEngineDirectory))
        {
            Directory.Delete(paths.AvatarEngineDirectory, recursive: true);
        }
    }

    /// <summary>Starts the installed Avatar Engine with the requested bridge binding.</summary>
    public async Task<AvatarEngineRuntime> StartAsync(string address, int port, CancellationToken cancellationToken)
    {
        EnsureWindows();
        using var lease = await LocalLifecycleLease.AcquireAsync(paths.RootDirectory, cancellationToken).ConfigureAwait(false);
        return await StartUnderLeaseAsync(address, port, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts while a coordinated replacement already owns the lifecycle lease.</summary>
    private async Task<AvatarEngineRuntime> StartUnderLeaseAsync(string address, int port, CancellationToken cancellationToken)
    {
        RecoverInterruptedReplacement();
        address = ValidateAddress(address);
        if (port is < 1 or > 65535)
        {
            throw new CliExitException(4, "Avatar Engine port must be between 1 and 65535.");
        }

        var executablePath = Path.Combine(paths.AvatarEngineDirectory, ExecutableName);
        if (!File.Exists(executablePath))
        {
            throw new CliExitException(1, "Avatar Engine is not installed. Run 'oscova avatar install' first.");
        }

        if ((await GetStatusAsync(cancellationToken).ConfigureAwait(false)).Running)
        {
            throw new CliExitException(1, "Avatar Engine is already running.");
        }

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = paths.AvatarEngineDirectory,
                UseShellExecute = true
            };
            // Unreal accepts -Key=value while Oscova exposes conventional long options.
            startInfo.ArgumentList.Add($"-BridgeAddress={address}");
            startInfo.ArgumentList.Add($"-BridgePort={port}");
            startInfo.ArgumentList.Add("-windowed");
            startInfo.ArgumentList.Add($"-ResX={AvatarSettings.DefaultResolutionWidth}");
            startInfo.ArgumentList.Add($"-ResY={AvatarSettings.DefaultResolutionHeight}");
            startInfo.ArgumentList.Add("-WinX=100");
            startInfo.ArgumentList.Add("-WinY=100");

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Avatar Engine process could not be started.");
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Avatar Engine exited immediately with code {process.ExitCode}.");
            }

            var runtime = new AvatarEngineRuntime(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                address,
                port);
            await WriteRuntimeAsync(runtime, cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            DeleteRuntimeFile();
            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>Returns the installed and running state of the Avatar Engine.</summary>
    public async Task<AvatarEngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var installed = File.Exists(Path.Combine(paths.AvatarEngineDirectory, ExecutableName));
        if (!OperatingSystem.IsWindows())
        {
            return new AvatarEngineStatus(false, installed, false, null, null, null);
        }

        var runtime = await ReadRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is null)
        {
            return new AvatarEngineStatus(true, installed, false, null, null, null, ReadReceipt(paths.AvatarEngineDirectory));
        }

        using var process = ResolveLiveProcess(runtime);
        if (process is null)
        {
            DeleteRuntimeFile();
            return new AvatarEngineStatus(true, installed, false, null, null, null, ReadReceipt(paths.AvatarEngineDirectory));
        }

        return new AvatarEngineStatus(true, installed, true, runtime.Pid, runtime.Address, runtime.Port, ReadReceipt(paths.AvatarEngineDirectory));
    }

    /// <summary>Queries publication independently of local installation and Windows support.</summary>
    public Task<AvatarReleaseIdentity> GetPublishedAsync(CancellationToken cancellationToken) => download.GetReleaseAsync(cancellationToken);

    /// <summary>Replaces reviewed content while keeping stop, install and relaunch under one lease.</summary>
    public async Task InstallOrUpdateAsync(AvatarReleaseIdentity accepted, Action<AvatarDownloadProgress> progress, CancellationToken cancellationToken)
    {
        EnsureWindows();
        using var lease = await LocalLifecycleLease.AcquireAsync(paths.RootDirectory, cancellationToken).ConfigureAwait(false);
        var published = await download.GetReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (!accepted.MatchesContent(published))
        {
            throw new CliExitException(1, "The Avatar Engine release changed after confirmation. Review its current release again.");
        }

        var before = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var installationCompleted = false;
        try
        {
            if (before.Running)
            {
                progress(new(0, accepted.SizeBytes, Message: "Stopping"));
                await StopAsync(cancellationToken).ConfigureAwait(false);
            }

            await InstallUnderStartupLeaseAsync(null, cancellationToken, progress, replace: true, accepted).ConfigureAwait(false);
            installationCompleted = true;
            if (before.Running)
            {
                progress(new(accepted.SizeBytes, accepted.SizeBytes, Message: "Restarting"));
                await StartUnderLeaseAsync(before.Address!, before.Port!.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception installationError)
        {
            // A failed replacement preserves the prior files, so restore their earlier running state once.
            if (!installationCompleted && before.Running && (await GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).Installed)
            {
                try { await StartUnderLeaseAsync(before.Address!, before.Port!.Value, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception restartError)
                {
                    throw new AggregateException("Avatar replacement failed and the previous engine could not restart.", installationError, restartError);
                }
            }

            throw;
        }
    }

    /// <summary>Checks content identity before the command asks for replacement consent.</summary>
    public async Task<AvatarInstallationCheck> CheckInstallationAsync(string? sourcePath, CancellationToken ct)
    {
        EnsureWindows();
        // Fresh installation lets the downloader own metadata retries and cancellation.
        if (!Directory.Exists(paths.AvatarEngineDirectory)) { return new("absent", null, null); }

        var installed = ReadReceipt(paths.AvatarEngineDirectory);
        var published = sourcePath is null ? await download.GetReleaseAsync(ct).ConfigureAwait(false) : await ReadLocalIdentityAsync(sourcePath, ct).ConfigureAwait(false);
        var state = !File.Exists(Path.Combine(paths.AvatarEngineDirectory, ExecutableName)) ? "incomplete" :
            installed is null ? "unknown" : installed.MatchesContent(published) ? "current" :
            installed.Version == published.Version ? "changed-release" : "update-available";
        return new(state, installed, published);
    }

    /// <summary>Checks once after launch and keeps an installed engine usable when the website is unavailable.</summary>
    public async Task<AvatarInstallationCheck> CheckAvailabilityAsync(CancellationToken ct)
    {
        EnsureWindows();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await CheckInstallationAsync(null, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new("check-unavailable", ReadReceipt(paths.AvatarEngineDirectory), null);
        }
    }

    private static async Task<AvatarReleaseIdentity> ReadLocalIdentityAsync(string sourcePath, CancellationToken ct)
    {
        if (Uri.TryCreate(sourcePath, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            throw new CliExitException(4, "Avatar Engine --source accepts a local ZIP only.");
        }

        if (!File.Exists(sourcePath)) { throw new CliExitException(4, "The local Avatar Engine ZIP does not exist."); }

        await using var file = File.OpenRead(sourcePath);
        var hash = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
        return new(null, "win-x64", file.Length, Convert.ToHexString(hash), "local-zip");
    }

    private static AvatarReleaseIdentity? ReadReceipt(string directory)
    {
        try
        {
            var path = Path.Combine(directory, ReceiptName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 65536) { return null; }

            var receipt = JsonSerializer.Deserialize<AvatarReleaseIdentity>(File.ReadAllBytes(path), CliJson.Options);
            return receipt?.IsValid() == true ? receipt : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void RecoverInterruptedReplacement()
    {
        if (!Directory.Exists(RecoveryDirectory)) { return; }

        var currentValid = File.Exists(Path.Combine(paths.AvatarEngineDirectory, ExecutableName)) && ReadReceipt(paths.AvatarEngineDirectory) is not null;
        if (currentValid) { DeleteTemporaryDirectory(RecoveryDirectory); return; }

        if (!Directory.Exists(paths.AvatarEngineDirectory) && File.Exists(Path.Combine(RecoveryDirectory, ExecutableName)))
        {
            Directory.Move(RecoveryDirectory, paths.AvatarEngineDirectory);
            return;
        }

        throw new CliExitException(1, $"Avatar replacement needs recovery. Preserve '{RecoveryDirectory}' and inspect the incomplete installation before retrying.");
    }

    /// <summary>Stops the Avatar Engine process recorded by Oscova.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        EnsureWindows();
        var runtime = await ReadRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is null)
        {
            return;
        }

        // Pin one process handle before checking its start time so PID reuse cannot retarget stop.
        using var process = ResolveLiveProcess(runtime);
        if (process is null)
        {
            DeleteRuntimeFile();
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        DeleteRuntimeFile();
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CliExitException(1, "Avatar Engine currently requires Windows. Install Oscova on a Windows renderer and run the avatar commands there.");
        }
    }

    private static string ValidateAddress(string address)
    {
        address = address?.Trim() ?? string.Empty;
        if (!IPAddress.TryParse(address, out _))
        {
            throw new CliExitException(4, "Avatar Engine address must be a valid IP address.");
        }

        return address;
    }

    private async Task WriteRuntimeAsync(AvatarEngineRuntime runtime, CancellationToken cancellationToken)
    {
        CliFileSecurity.EnsurePrivateDirectory(paths.AvatarDirectory);
        var temporaryPath = paths.AvatarRuntimeFilePath + ".tmp";
        try
        {
            var stream = File.Create(temporaryPath);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, runtime, CliJson.Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, paths.AvatarRuntimeFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<AvatarEngineRuntime?> ReadRuntimeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.AvatarRuntimeFilePath))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(paths.AvatarRuntimeFilePath);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<AvatarEngineRuntime>(stream, CliJson.Options, cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("The Avatar Engine runtime identity is empty.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliExitException(1, $"Avatar Engine runtime identity cannot be read. Resolve '{paths.AvatarRuntimeFilePath}' before stopping or erasing.");
        }
    }

    private static Process? ResolveLiveProcess(AvatarEngineRuntime runtime)
    {
        if (runtime.StartedAtUtc == default)
        {
            return null;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(runtime.Pid);
            if (OperatingSystem.IsWindows()) { _ = process.SafeHandle; }

            var processStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if (process.HasExited || processStartedAtUtc != runtime.StartedAtUtc)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            process?.Dispose();
            return null;
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }

    private void DeleteRuntimeFile()
    {
        if (File.Exists(paths.AvatarRuntimeFilePath))
        {
            File.Delete(paths.AvatarRuntimeFilePath);
        }
    }

    private void CheckExpandedFreeSpace(string archivePath)
    {
        long expandedSize = 0;
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                expandedSize = checked(expandedSize + entry.Length);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            throw new CliExitException(1, "The downloaded Avatar Engine ZIP is invalid. Run the installation again.");
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(paths.AvatarEngineDirectory));
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace < expandedSize)
            {
                throw new CliExitException(1, "There is not enough disk space to extract the Avatar Engine.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Extraction remains authoritative when the platform cannot report capacity.
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CliRender.Failure(
                $"Avatar Engine cleanup warning: temporary files remain at '{path}'.",
                "Close any process using them, then remove that directory.");
        }
    }

    private static async Task MoveDirectoryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(sourcePath, destinationPath);
                return;
            }
            catch (Exception exception) when (
                attempt < 9 && exception is IOException or UnauthorizedAccessException)
            {
                // Windows security scanners can briefly lock freshly extracted Unreal binaries.
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
