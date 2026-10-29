using Oscova.Agents.Core.Cli;
using Oscova.Agents.Host.Hosting;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Inspects the CLI-visible host, bearer, and local-control surfaces without changing state.
/// </summary>
public sealed class CliSurfaceInspector(
    StartCollisionChecker collisionChecker,
    CliAuthFileStore authStore,
    CliConfigStore configStore,
    IHttpClientFactory httpClientFactory)
{
    /// <summary>
    /// Reports which host and auth surfaces this command invocation would use.
    /// </summary>
    public async Task<CliSurfaceReport> InspectAsync(
        OscovaCommandSettings settings,
        CancellationToken cancellationToken,
        bool strictHostProbe = false)
    {
        var config = configStore.Read();
        var auth = await authStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        var targetHost = ResolveTargetHost(settings, config, auth);
        var targetIsLoopback = CliHostUrlSelector.IsLoopback(targetHost);
        var descriptor = await collisionChecker.FindRunningHostAsync(cancellationToken).ConfigureAwait(false);
        if (!targetIsLoopback || descriptor is not null && !CliHostUrlSelector.PortMatchesAny(targetHost, descriptor.Urls))
        {
            descriptor = null;
        }

        var fallbackHostRunning = descriptor is null &&
            targetIsLoopback &&
            await IsKnownLoopbackHostReachableAsync(targetHost, strictHostProbe, cancellationToken).ConfigureAwait(false);
        var localControlAvailable = IsLocalControlAvailable(targetHost, targetIsLoopback, descriptor);
        var bearer = ResolveBearerState(auth, targetHost);

        return new CliSurfaceReport(
            targetHost,
            targetIsLoopback,
            descriptor is not null || fallbackHostRunning,
            descriptor?.Pid,
            descriptor?.Urls ?? (fallbackHostRunning ? [targetHost] : []),
            descriptor?.StartedAtUtc,
            localControlAvailable,
            bearer,
            localControlAvailable ? "control-token" : bearer.LoggedIn ? "bearer" : "none",
            bearer.LoggedIn ? "bearer" : "login-required");
    }

    private static string ResolveTargetHost(OscovaCommandSettings settings, CliConfigFile config, CliAuthFile? auth)
    {
        var target = settings.Host
            ?? Environment.GetEnvironmentVariable("OSCOVA_HOST")
            ?? config.Host
            ?? auth?.CurrentHost
            ?? CliHostUrlSelector.DefaultLocalHost;

        try
        {
            return CliHostResolver.NormalizeHostUrl(target);
        }
        catch (CliExitException)
        {
            return target;
        }
    }

    private static bool IsLocalControlAvailable(string targetHost, bool targetIsLoopback, RuntimeDescriptor? descriptor)
    {
        return targetIsLoopback &&
            descriptor is not null &&
            !string.IsNullOrWhiteSpace(descriptor.ControlToken) &&
            CliHostUrlSelector.PortMatchesAny(targetHost, descriptor.Urls);
    }

    /// <summary>
    /// Verifies a selected loopback target is alive when runtime.json is absent.
    /// </summary>
    private async Task<bool> IsKnownLoopbackHostReachableAsync(
        string targetHost,
        bool strictHostProbe,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Dual-stack localhost refusal can take over three seconds on Windows.
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = httpClientFactory.CreateClient();
        try
        {
            var requestUri = new Uri(new Uri(targetHost.TrimEnd('/') + "/"), "api/host/version");
            using var response = await client.GetAsync(requestUri, timeout.Token).ConfigureAwait(false);
            if (strictHostProbe)
            {
                response.EnsureSuccessStatusCode();
            }

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception) when (
            strictHostProbe &&
            exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            return false;
        }
        catch (Exception) when (!strictHostProbe)
        {
            return false;
        }
    }

    private static CliBearerState ResolveBearerState(CliAuthFile? auth, string targetHost)
    {
        var entry = FindAuthEntry(auth, targetHost);
        return entry is null || string.IsNullOrWhiteSpace(entry.Token)
            ? new CliBearerState(false, null, null, null)
            : new CliBearerState(true, entry.IdentityUserName, entry.Host, entry.ExpiresAtUtc);
    }

    private static CliAuthHostEntry? FindAuthEntry(CliAuthFile? auth, string targetHost)
    {
        if (auth?.Hosts is null)
        {
            return null;
        }

        foreach (var entry in auth.Hosts)
        {
            try
            {
                if (string.Equals(CliHostResolver.NormalizeHostUrl(entry.Host), targetHost, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            catch (CliExitException)
            {
            }
        }

        return null;
    }
}

/// <summary>
/// Describes the target host, local runtime descriptor, and auth paths visible to CLI commands.
/// </summary>
public sealed record CliSurfaceReport(
    string TargetHostUrl,
    bool TargetIsLoopback,
    bool HostRunning,
    int? Pid,
    string[] Urls,
    DateTimeOffset? StartedAtUtc,
    bool LocalControlAvailable,
    CliBearerState Bearer,
    string LifecyclePath,
    string ChatPath);

/// <summary>
/// Describes the bearer login entry selected for the effective CLI target host.
/// </summary>
public sealed record CliBearerState(
    bool LoggedIn,
    string? UserName,
    string? HostUrl,
    DateTimeOffset? ExpiresAtUtc);
