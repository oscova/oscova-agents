using System.Diagnostics;
using System.Net.Http.Json;
using Oscova.Agents.Core.ApiContracts.HostControl;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Host.Hosting;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Utils;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Controls the live same-machine host by runtime token instead of user login.
/// </summary>
public sealed class LocalHostControl(
    CliConfigStore configStore,
    HostApiClient apiClient,
    StartCollisionChecker collisionChecker,
    AvatarMcpManager avatarMcpManager)
{
    /// <summary>
    /// Returns the live local host descriptor when the effective command target is same-machine.
    /// </summary>
    public async Task<RuntimeDescriptor?> TryResolveLocalAsync(OscovaCommandSettings settings, CancellationToken ct, bool allowUnresponsive = false)
    {
        var explicitHost = settings.Host ?? Environment.GetEnvironmentVariable("OSCOVA_HOST");
        var normalizedExplicitHost = NormalizeOrNull(explicitHost);
        if (!string.IsNullOrWhiteSpace(explicitHost) &&
            (normalizedExplicitHost is null || !CliHostUrlSelector.IsLoopback(normalizedExplicitHost)))
        {
            return null;
        }

        var configuredHost = explicitHost is null ? configStore.Read().Host : null;
        var normalizedConfiguredHost = NormalizeOrNull(configuredHost);
        if (!string.IsNullOrWhiteSpace(configuredHost) &&
            (normalizedConfiguredHost is null || !CliHostUrlSelector.IsLoopback(normalizedConfiguredHost)))
        {
            return null;
        }

        var descriptor = allowUnresponsive ? RuntimeDescriptorWriter.ReadOrNull() :
            await collisionChecker.FindRunningHostAsync(ct).ConfigureAwait(false);
        if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.ControlToken))
        {
            return null;
        }

        // A loopback target only controls the live host when its port matches. A different port means
        // the user meant another local instance or a port-forward to a remote, so defer to the bearer path.
        if (normalizedExplicitHost is not null && !CliHostUrlSelector.PortMatchesAny(normalizedExplicitHost, descriptor.Urls))
        {
            return null;
        }

        if (normalizedConfiguredHost is not null && !CliHostUrlSelector.PortMatchesAny(normalizedConfiguredHost, descriptor.Urls))
        {
            return null;
        }

        if (allowUnresponsive)
        {
            try
            {
                using var process = Process.GetProcessById(descriptor.Pid);
                if (process.HasExited) { return null; }

                if (descriptor.ProcessStartIdentifier != 0 &&
                    ProcessIdentity.GetStartIdentifier(process) != descriptor.ProcessStartIdentifier)
                {
                    throw new CliExitException(1, "The local Host process identity changed. Check 'oscova status' before stopping it.");
                }
            }
            catch (ArgumentException) { return null; }
        }

        return descriptor;
    }

    /// <summary>
    /// Requests local shutdown, falling back to process-tree termination if HTTP control is unavailable.
    /// </summary>
    public async Task StopAsync(RuntimeDescriptor descriptor, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(descriptor.Pid);
        }
        catch (ArgumentException)
        {
            return;
        }

        using var ownedProcess = process;
        if (OperatingSystem.IsWindows()) { _ = process.SafeHandle; }

        if (process.HasExited) { return; }

        if (descriptor.StartedAtUtc == default ||
            (descriptor.ProcessStartIdentifier != 0 &&
             ProcessIdentity.GetStartIdentifier(process) != descriptor.ProcessStartIdentifier))
        {
            throw new CliExitException(1, "The local Host process identity changed. Run 'oscova status' before trying again.");
        }

        if (!await TryPostAsync(descriptor, "api/host/shutdown", ct).ConfigureAwait(false))
        {
            if (process.HasExited) { return; }

            var current = RuntimeDescriptorWriter.ReadOrNull();
            // Earlier Hosts can authenticate HTTP shutdown but cannot prove identity for force termination.
            if (descriptor.ProcessStartIdentifier == 0 || current?.Pid != descriptor.Pid ||
                current.ProcessStartIdentifier != descriptor.ProcessStartIdentifier || current.StartedAtUtc != descriptor.StartedAtUtc ||
                string.IsNullOrWhiteSpace(descriptor.ControlToken) || current.ControlToken != descriptor.ControlToken ||
                string.IsNullOrWhiteSpace(descriptor.ExecutablePath) ||
                !PathHelper.ArePathsEqual(process.MainModule?.FileName ?? string.Empty, descriptor.ExecutablePath))
            {
                throw new CliExitException(1, "The local Host could not be verified for shutdown. Check 'oscova status'.");
            }

            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
        }

        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CliExitException(1, "Oscova Host is still running after 60 seconds. No shutdown was confirmed.");
        }
    }

    /// <summary>Stops the owned MCP and Host and reports whether either process needed stopping.</summary>
    public async Task<bool> StopLocalServicesAsync(RuntimeDescriptor? descriptor, CancellationToken ct)
    {
        var mcpStopped = await avatarMcpManager.StopAsync(ct).ConfigureAwait(false);
        if (descriptor is not null)
        {
            await StopAsync(descriptor, ct).ConfigureAwait(false);
        }

        return descriptor is not null || mcpStopped;
    }

    /// <summary>
    /// Requests local restart through the running host so it can relaunch itself.
    /// </summary>
    public async Task RestartAsync(RuntimeDescriptor descriptor, CancellationToken ct)
    {
        if (!await TryPostAsync(descriptor, "api/host/restart", ct).ConfigureAwait(false))
        {
            throw new CliExitException(1, "The local host did not accept the restart request.");
        }
    }

    /// <summary>
    /// Reads local host status with the runtime control token.
    /// </summary>
    public async Task<HostStatusResponse> StatusAsync(RuntimeDescriptor descriptor, CancellationToken ct)
    {
        var url = CliHostUrlSelector.SelectLocalHost(descriptor.Urls);
        using var client = apiClient.CreateLocalControl(url, descriptor.ControlToken);
        using var response = await client.GetAsync("api/host/status", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HostStatusResponse>(CliJson.Options, ct)
.ConfigureAwait(false) ?? throw new CliExitException(1, "The host returned an empty status response.");
    }

    /// <summary>
    /// Selects the loopback URL that should be shown and called by local CLI control.
    /// </summary>
    public static string SelectLocalHost(RuntimeDescriptor descriptor) => CliHostUrlSelector.SelectLocalHost(descriptor.Urls);

    private async Task<bool> TryPostAsync(RuntimeDescriptor descriptor, string path, CancellationToken ct)
    {
        try
        {
            var url = CliHostUrlSelector.SelectLocalHost(descriptor.Urls);
            using var client = apiClient.CreateLocalControl(url, descriptor.ControlToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.PostAsync(path, null, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static string? NormalizeOrNull(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        try
        {
            return CliHostResolver.NormalizeHostUrl(host);
        }
        catch (CliExitException)
        {
            return null;
        }
    }
}
