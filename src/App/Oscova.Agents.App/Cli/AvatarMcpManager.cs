using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oscova.Agents.App.AvatarMcp;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Avatar;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Settings;
using Oscova.Agents.Utils;

namespace Oscova.Agents.App.Cli;

/// <summary>Manages the independently detached Avatar MCP process for this CLI installation.</summary>
public sealed class AvatarMcpManager(CliPaths paths, ILoggerFactory loggerFactory)
{
    /// <summary>Starts MCP without starting the Avatar Engine or changing a running service.</summary>
    public async Task StartAsync(int port, string? avatarUrl, CancellationToken cancellationToken)
    {
        ValidateOptions(port, avatarUrl);
        // Hold launch ownership through readiness so concurrent starts cannot queue detached children.
        using var launchLease = await AvatarFileLease.AcquireAsync(
            Path.Combine(LocalLifecycleLease.GetDirectory(paths.RootDirectory), "mcp-start.lock"), cancellationToken).ConfigureAwait(false);
        var appDataPath = PathUtil.AppDataPath;
        var endpoint = $"http://127.0.0.1:{port}/mcp";
        var existing = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await RequireMatchingAsync(existing, endpoint, appDataPath, avatarUrl, cancellationToken).ConfigureAwait(false);
            return;
        }

        var instanceId = Guid.NewGuid();
        var childArgs = new List<string>
        {
            "avatar", "mcp", "run", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--app-data", appDataPath, "--instance-id", instanceId.ToString("D"), "--no-interactive"
        };
        if (avatarUrl is not null) childArgs.AddRange(["--avatar-url", avatarUrl]);
        using var launchedProcess = await DetachedProcessLauncher.LaunchAsync(childArgs, cancellationToken).ConfigureAwait(false);
        try
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runtime = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (runtime is not null)
                {
                    if (runtime.InstanceId == instanceId) return;
                    // A simultaneous matching launch can win, but it is never ours to terminate.
                    await RequireMatchingAsync(runtime, endpoint, appDataPath, avatarUrl, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (launchedProcess is { HasExited: true }) break;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            throw new CliExitException(1, $"Avatar MCP did not become ready. Check {LogDirectory}.");
        }
        catch
        {
            var owned = await ReadRuntimeAsync(CancellationToken.None).ConfigureAwait(false);
            using var recordedProcess = owned?.InstanceId == instanceId ? ResolveLiveProcess(owned) : null;
            var process = launchedProcess ?? recordedProcess;
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            }

            if (process is null)
                throw new CliExitException(1, $"Avatar MCP failed to start and child cleanup could not be verified. Check {LogDirectory} and run avatar mcp status.");
            throw;
        }
    }

    /// <summary>Returns a ready managed process, not the downstream Avatar's connection state.</summary>
    public async Task<AvatarMcpRuntime?> GetStatusAsync(CancellationToken cancellationToken)
    {
        var runtime = await ReadRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is null) return null;
        using var process = ResolveLiveProcess(runtime);
        if (process is null) return null;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            var health = await client.GetFromJsonAsync<AvatarMcpRuntime>(
                new Uri(new Uri(runtime.Endpoint), "/health"), CliJson.Options, cancellationToken).ConfigureAwait(false);
            return health == runtime ? runtime : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Stops only the recorded process and preserves all Avatar settings and credentials.</summary>
    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        var runtime = await ReadRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is null)
        {
            if (File.Exists(paths.AvatarMcpRuntimeFilePath))
            {
                throw new CliExitException(1, "Avatar MCP runtime identity is invalid. Resolve the running process before stopping or erasing.");
            }

            return false;
        }

        using var process = ResolveLiveProcess(runtime);
        if (process is not null)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
        // Serialize deletion with the next child's descriptor publication using the existing lifetime lease.
        using var lease = await AvatarFileLease.AcquireAsync(Path.Combine(paths.AvatarDirectory, "mcp-runtime.lock"), cancellationToken).ConfigureAwait(false);
        await DeleteOwnedRuntimeAsync(runtime.InstanceId).ConfigureAwait(false);
        return process is not null;
    }

    /// <summary>Reads the current saved target from the same root used by the running MCP process.</summary>
    public async Task<string> ReadAvatarUrlAsync(string appDataPath, CancellationToken cancellationToken)
    {
        using var lifecycleLease = await LocalLifecycleLease.AcquireAsync(paths.RootDirectory, cancellationToken).ConfigureAwait(false);
        var settingsPath = Path.Combine(appDataPath, "settings");
        using var lease = await AvatarFileLease.AcquireAsync(Path.Combine(settingsPath, "avatar.yaml.lock"), cancellationToken).ConfigureAwait(false);
        var settings = new AvatarSettings(new FileSettingsRepository(_ => settingsPath), loggerFactory);
        await settings.ReloadAsync().ConfigureAwait(false);
        return settings.BridgeUrl;
    }

    /// <summary>Runs the child until shutdown while retaining exclusive ownership of its runtime record.</summary>
    public async Task RunAsync(int port, string appDataPath, Guid instanceId, string? avatarUrl, CancellationToken cancellationToken)
    {
        ValidateOptions(port, avatarUrl);
        if (!Path.IsPathFullyQualified(appDataPath) || instanceId == Guid.Empty)
            throw new CliExitException(4, "The MCP child requires an absolute app-data path and an instance ID.");
        Environment.SetEnvironmentVariable(PathUtil.AppDataDirectoryEnvironmentVariable, appDataPath);
        using var lifecycleLease = await LocalLifecycleLease.AcquireAsync(paths.RootDirectory, cancellationToken).ConfigureAwait(false);
        CliFileSecurity.EnsurePrivateDirectory(appDataPath);
        paths.EnsureRootDirectory();
        CliFileSecurity.EnsurePrivateDirectory(paths.AvatarDirectory);
        using var lease = await AvatarFileLease.AcquireAsync(Path.Combine(paths.AvatarDirectory, "mcp-runtime.lock"), cancellationToken).ConfigureAwait(false);
        var ready = false;
        await using var app = AvatarMcpHost.Create(port, () => Volatile.Read(ref ready));
        using var process = Process.GetCurrentProcess();
        var runtime = new AvatarMcpRuntime(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), instanceId, $"http://127.0.0.1:{port}/mcp", appDataPath);
        app.MapGet("/health", () => Results.Json(runtime, CliJson.Options));
        try
        {
            // Bind first, then persist the target. Failed or competing starts cannot change pairing settings.
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            if (avatarUrl is not null)
                await app.Services.GetRequiredService<AvatarRuntimeSettingsService>().SaveBridgeUrlAsync(avatarUrl).ConfigureAwait(false);
            await FileUtil.WriteAllTextAtomicAsync(paths.AvatarMcpRuntimeFilePath, JsonSerializer.Serialize(runtime, CliJson.Options), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref ready, true);
            lifecycleLease.Dispose();
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            NLog.LogManager.GetLogger(typeof(AvatarMcpManager).FullName!).Error(exception, "Avatar MCP child failed on port {Port}.", port);
            throw;
        }
        finally
        {
            Volatile.Write(ref ready, false);
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await DeleteOwnedRuntimeAsync(instanceId).ConfigureAwait(false);
            // Flush startup diagnostics before releasing ownership of writable environment files.
            NLog.LogManager.Shutdown();
        }
    }

    private static string LogDirectory => PathUtil.SystemLogsPath;

    private static void ValidateOptions(int port, string? avatarUrl)
    {
        if (port is < 1 or > 65535) throw new CliExitException(4, "MCP port must be between 1 and 65535.");
        if (avatarUrl is not null && (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
            throw new CliExitException(4, "Avatar URL must be an absolute HTTP or HTTPS URL without credentials, a query, or a fragment.");
    }

    private async Task RequireMatchingAsync(AvatarMcpRuntime runtime, string endpoint, string appDataPath, string? avatarUrl, CancellationToken cancellationToken)
    {
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (runtime.Endpoint != endpoint || !string.Equals(runtime.AppDataPath, appDataPath, pathComparison) ||
            avatarUrl is not null && !string.Equals(avatarUrl.TrimEnd('/'),
                (await ReadAvatarUrlAsync(appDataPath, cancellationToken).ConfigureAwait(false)).TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new CliExitException(4, "Avatar MCP is already running with different settings. Stop it before starting with a different port, app-data root, or Avatar URL.");
    }

    private async Task<AvatarMcpRuntime?> ReadRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var stream = File.OpenRead(paths.AvatarMcpRuntimeFilePath);
            var runtime = await JsonSerializer.DeserializeAsync<AvatarMcpRuntime>(stream, CliJson.Options, cancellationToken).ConfigureAwait(false);
            // A descriptor is local data, not authority to probe an arbitrary network address.
            if (runtime is null || runtime.Pid <= 0 || runtime.StartedAtUtc == default || runtime.InstanceId == Guid.Empty ||
                !Path.IsPathFullyQualified(runtime.AppDataPath) || !Uri.TryCreate(runtime.Endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/mcp" ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
            return runtime;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private async Task DeleteOwnedRuntimeAsync(Guid instanceId)
    {
        var current = await ReadRuntimeAsync(CancellationToken.None).ConfigureAwait(false);
        if (current?.InstanceId == instanceId) File.Delete(paths.AvatarMcpRuntimeFilePath);
    }

    private static Process? ResolveLiveProcess(AvatarMcpRuntime runtime)
    {
        var process = TryGetProcess(runtime.Pid);
        if (process is null) return null;
        try
        {
            if (OperatingSystem.IsWindows()) _ = process.SafeHandle;
            if (!process.HasExited && new DateTimeOffset(process.StartTime.ToUniversalTime()) == runtime.StartedAtUtc) return process;
        }
        catch (InvalidOperationException) { }
        catch
        {
            process.Dispose();
            throw;
        }

        process.Dispose();
        return null;
    }

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>Identifies the owned MCP process without storing downstream credentials.</summary>
public sealed record AvatarMcpRuntime(int Pid, DateTimeOffset StartedAtUtc, Guid InstanceId, string Endpoint, string AppDataPath);
