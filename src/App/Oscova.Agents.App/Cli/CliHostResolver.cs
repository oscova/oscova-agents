using Oscova.Agents.Core.Cli;

namespace Oscova.Agents.App.Cli;

public sealed class CliHostResolver(CliConfigStore configStore, CliAuthFileStore authStore, StartCollisionChecker collisionChecker)
{
    public async Task<ResolvedHost> ResolveAsync(OscovaCommandSettings settings, bool requireToken, CancellationToken cancellationToken)
    {
        var config = configStore.Read();
        var auth = await authStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        var explicitHost = settings.Host ?? Environment.GetEnvironmentVariable("OSCOVA_HOST");
        var host = explicitHost
            ?? config.Host
            ?? auth?.CurrentHost
            ?? CliHostUrlSelector.DefaultLocalHost;

        host = NormalizeHostUrl(host);
        var entry = FindAuthEntry(auth, host);

        if (requireToken && string.IsNullOrWhiteSpace(entry?.Token))
        {
            var liveHost = await TryResolveLiveAuthenticatedLocalHostAsync(explicitHost, config.Host, auth, cancellationToken).ConfigureAwait(false);
            if (liveHost is not null)
            {
                return liveHost;
            }

            throw new CliExitException(2, $"Run 'oscova login --host {host}' first.");
        }

        return new ResolvedHost(host, entry?.Token, entry);
    }

    private async Task<ResolvedHost?> TryResolveLiveAuthenticatedLocalHostAsync(
        string? explicitHost,
        string? configuredHost,
        CliAuthFile? auth,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitHost) || !ShouldRepairConfiguredHost(configuredHost))
        {
            return null;
        }

        var descriptor = await collisionChecker.FindRunningHostAsync(cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return null;
        }

        var host = NormalizeHostUrl(CliHostUrlSelector.SelectLocalHost(descriptor.Urls));
        var entry = FindAuthEntry(auth, host);
        if (string.IsNullOrWhiteSpace(entry?.Token))
        {
            return null;
        }

        configStore.SetHost(host);
        return new ResolvedHost(host, entry.Token, entry);
    }

    private static bool ShouldRepairConfiguredHost(string? configuredHost)
    {
        if (string.IsNullOrWhiteSpace(configuredHost))
        {
            return true;
        }

        try
        {
            return new Uri(NormalizeHostUrl(configuredHost)).IsLoopback;
        }
        catch (CliExitException)
        {
            return true;
        }
    }

    private static CliAuthHostEntry? FindAuthEntry(CliAuthFile? auth, string normalizedHost)
    {
        if (auth?.Hosts is null)
        {
            return null;
        }

        foreach (var entry in auth.Hosts)
        {
            try
            {
                if (string.Equals(NormalizeHostUrl(entry.Host), normalizedHost, StringComparison.OrdinalIgnoreCase))
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

    public static string NormalizeHostUrl(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return CliHostUrlSelector.DefaultLocalHost;
        }

        var trimmed = host.Trim().TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new CliExitException(4, $"'{host}' is not a valid host URL.");
        }

        return uri.ToString().TrimEnd('/');
    }
}

public sealed record ResolvedHost(string HostUrl, string? Token, CliAuthHostEntry? AuthEntry);