using System.Net.Http.Json;
using Oscova.Agents.Core.ApiContracts.CliAuth;
using Oscova.Agents.Core.Cli;
using Oscova.Agents.Host.Hosting;
using Spectre.Console;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Runs device-code CLI login and persists the resulting bearer entry for a resolved host.
/// </summary>
public sealed class CliDeviceLoginFlow(
    HostApiClient apiClient,
    CliAuthFileStore authStore,
    CliConfigStore configStore,
    StartCollisionChecker collisionChecker)
{
    private static readonly TimeSpan ReplacementWaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReplacementWaitDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Returns the stored CLI auth entry and effective host on success; throws with auth exit code on denial or timeout.
    /// </summary>
    public async Task<CliDeviceLoginResult> LoginAsync(ResolvedHost host, bool jsonOutput, bool quiet, CancellationToken cancellationToken)
    {
        var activeHost = host;
        var continuationUsed = false;

        while (true)
        {
            var attemptDescriptor = await TryReadLocalRuntimeAsync(activeHost, cancellationToken).ConfigureAwait(false);
            var initialTokens = (await authStore.ReadAsync(cancellationToken).ConfigureAwait(false))?.Hosts?
                .Select(entry => entry.Token).ToHashSet(StringComparer.Ordinal) ?? [];
            using var client = apiClient.Create(activeHost);

            StartCliDeviceLoginResponse start;
            try
            {
                var startResponse = await client.PostAsJsonAsync("api/auth/cli/device/start", new StartCliDeviceLoginRequest
                {
                    Client = "oscova-cli",
                    Machine = Environment.MachineName
                }, CliJson.Options, cancellationToken).ConfigureAwait(false);
                startResponse.EnsureSuccessStatusCode();
                start = await startResponse.Content.ReadFromJsonAsync<StartCliDeviceLoginResponse>(CliJson.Options, cancellationToken)
.ConfigureAwait(false) ?? throw new CliExitException(1, "The host returned an empty login response.");
            }
            catch (Exception ex) when (IsConnectionFailure(ex, cancellationToken))
            {
                var replacementHost = await TryResolveReplacementHostAsync(
                    activeHost,
                    attemptDescriptor,
                    continuationUsed,
                    waitForReplacement: true,
                    activity: null,
                    quiet || jsonOutput,
                    cancellationToken).ConfigureAwait(false);
                if (replacementHost is null)
                {
                    throw;
                }

                activeHost = replacementHost;
                continuationUsed = true;
                continue;
            }

            CliActivity? activity = null;
            if (jsonOutput)
            {
                await CliOutput.WriteJsonAsync(start, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var instructions = new Grid()
                    .AddColumn()
                    .AddRow(new Markup($"Open [link]{start.VerificationUri.EscapeMarkup()}[/]"))
                    .AddRow(new Markup(
                        $"Approval code [bold]{start.UserCode.EscapeMarkup()}[/] is already included in the link."));
                AnsiConsole.Write(CliRender.Card("Sign in", instructions, CliTheme.Accent));

                var showActivity = !quiet &&
                    AnsiConsole.Profile.Capabilities.Interactive &&
                    AnsiConsole.Profile.Capabilities.Ansi;
                activity = new CliActivity(showActivity, "Waiting for browser approval...");
            }

            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(start.ExpiresInSeconds);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var stored = await TryUseStoredLocalAuthAsync(activeHost, initialTokens, cancellationToken).ConfigureAwait(false);
                    if (stored is not null)
                    {
                        return stored;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, start.IntervalSeconds)), cancellationToken).ConfigureAwait(false);

                    PollCliDeviceLoginResponse poll;
                    try
                    {
                        var pollResponse = await client.PostAsJsonAsync("api/auth/cli/device/poll", new PollCliDeviceLoginRequest
                        {
                            DeviceCode = start.DeviceCode
                        }, CliJson.Options, cancellationToken).ConfigureAwait(false);
                        pollResponse.EnsureSuccessStatusCode();
                        poll = await pollResponse.Content.ReadFromJsonAsync<PollCliDeviceLoginResponse>(CliJson.Options, cancellationToken)
.ConfigureAwait(false) ?? throw new CliExitException(1, "The host returned an empty login polling response.");
                    }
                    catch (Exception ex) when (IsConnectionFailure(ex, cancellationToken))
                    {
                        var replacementHost = await TryResolveReplacementHostAsync(
                            activeHost,
                            attemptDescriptor,
                            continuationUsed,
                            waitForReplacement: true,
                            activity,
                            quiet || jsonOutput,
                            cancellationToken).ConfigureAwait(false);
                        if (replacementHost is null)
                        {
                            throw;
                        }

                        activeHost = replacementHost;
                        continuationUsed = true;
                        break;
                    }

                    var status = string.IsNullOrWhiteSpace(poll.Status) ? "failed" : poll.Status;
                    if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(poll.Token))
                    {
                        if (string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
                        {
                            var replacementHost = await TryResolveReplacementHostAsync(
                                activeHost,
                                attemptDescriptor,
                                continuationUsed,
                                waitForReplacement: false,
                                activity,
                                quiet || jsonOutput,
                                cancellationToken).ConfigureAwait(false);
                            if (replacementHost is not null)
                            {
                                activeHost = replacementHost;
                                continuationUsed = true;
                                break;
                            }
                        }

                        throw new CliExitException(2, $"Login {status}.");
                    }

                    var entry = new CliAuthHostEntry
                    {
                        Host = activeHost.HostUrl,
                        Token = poll.Token,
                        IdentityUserId = poll.IdentityUserId ?? string.Empty,
                        IdentityUserName = poll.IdentityUserName ?? string.Empty,
                        IssuedAtUtc = poll.IssuedAtUtc ?? DateTimeOffset.UtcNow,
                        ExpiresAtUtc = poll.ExpiresAtUtc,
                        Scopes = ["cli"]
                    };

                    await authStore.UpsertAsync(activeHost.HostUrl, entry, cancellationToken).ConfigureAwait(false);
                    return new CliDeviceLoginResult(activeHost.HostUrl, entry);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    var stored = await TryUseStoredLocalAuthAsync(activeHost, initialTokens, cancellationToken).ConfigureAwait(false);
                    if (stored is not null)
                    {
                        return stored;
                    }

                    throw new CliExitException(2, "Login timed out.");
                }
            }
            finally
            {
                if (activity is not null)
                {
                    await activity.StopAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<CliDeviceLoginResult?> TryUseStoredLocalAuthAsync(
        ResolvedHost activeHost,
        IReadOnlySet<string> initialTokens,
        CancellationToken cancellationToken)
    {
        var entry = await authStore.FindAsync(activeHost.HostUrl, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(entry?.Token) &&
            !initialTokens.Contains(entry.Token))
        {
            return new CliDeviceLoginResult(activeHost.HostUrl, entry);
        }

        var runtimeHost = await TrySelectLiveLocalHostAsync(activeHost, cancellationToken).ConfigureAwait(false);
        if (runtimeHost is null ||
            string.Equals(runtimeHost, activeHost.HostUrl, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        entry = await authStore.FindAsync(runtimeHost, cancellationToken).ConfigureAwait(false);
        // A token saved before this login began does not prove fresh browser approval.
        if (string.IsNullOrWhiteSpace(entry?.Token) ||
            initialTokens.Contains(entry.Token))
        {
            return null;
        }

        configStore.SetHost(runtimeHost);
        return new CliDeviceLoginResult(runtimeHost, entry);
    }

    private async Task<ResolvedHost?> TryResolveReplacementHostAsync(
        ResolvedHost activeHost,
        RuntimeDescriptor? attemptDescriptor,
        bool continuationUsed,
        bool waitForReplacement,
        CliActivity? activity,
        bool quiet,
        CancellationToken cancellationToken)
    {
        if (continuationUsed || !CliHostUrlSelector.IsLoopback(activeHost.HostUrl))
        {
            return null;
        }

        var deadline = waitForReplacement
            ? DateTimeOffset.UtcNow.Add(ReplacementWaitTimeout)
            : DateTimeOffset.UtcNow;
        while (true)
        {
            var descriptor = await TryReadLocalRuntimeAsync(activeHost, cancellationToken).ConfigureAwait(false);
            if (descriptor is not null)
            {
                var replacementHost = CliHostResolver.NormalizeHostUrl(CliHostUrlSelector.SelectLocalHost(descriptor.Urls));
                var hostChanged = !string.Equals(replacementHost, activeHost.HostUrl, StringComparison.OrdinalIgnoreCase);
                var runtimeChanged = attemptDescriptor is not null &&
                    (descriptor.Pid != attemptDescriptor.Pid || descriptor.StartedAtUtc != attemptDescriptor.StartedAtUtc);
                if (hostChanged || runtimeChanged)
                {
                    configStore.SetHost(replacementHost);
                    if (activity is not null)
                    {
                        await activity.StopAsync().ConfigureAwait(false);
                    }

                    CliOutput.Info(
                        "Oscova restarted while this login was waiting. If you restored a backup, this is expected.",
                        quiet);
                    return new ResolvedHost(replacementHost, null, null);
                }
            }

            if (!waitForReplacement || DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(ReplacementWaitDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> TrySelectLiveLocalHostAsync(ResolvedHost activeHost, CancellationToken cancellationToken)
    {
        var descriptor = await TryReadLocalRuntimeAsync(activeHost, cancellationToken).ConfigureAwait(false);
        return descriptor is null
            ? null
            : CliHostResolver.NormalizeHostUrl(CliHostUrlSelector.SelectLocalHost(descriptor.Urls));
    }

    private async Task<RuntimeDescriptor?> TryReadLocalRuntimeAsync(ResolvedHost activeHost, CancellationToken cancellationToken)
    {
        return CliHostUrlSelector.IsLoopback(activeHost.HostUrl)
            ? await collisionChecker.FindRunningHostAsync(cancellationToken)
.ConfigureAwait(false) : null;
    }

    private static bool IsConnectionFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception is HttpRequestException { StatusCode: null } ||
            exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
    }
}

/// <summary>
/// Describes a successful CLI login and the host URL that should be used after local retargeting.
/// </summary>
public sealed record CliDeviceLoginResult(string HostUrl, CliAuthHostEntry Entry);
