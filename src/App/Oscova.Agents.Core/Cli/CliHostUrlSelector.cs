using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.Cli;

/// <summary>
/// Chooses the local URL that same-user CLI tooling should use for a running Oscova host.
/// </summary>
public static class CliHostUrlSelector
{
    public static readonly string DefaultLocalHost = $"http://localhost:{NetworkSettings.DefaultPort}";

    /// <summary>
    /// Returns true when the normalized URL targets a loopback address.
    /// </summary>
    public static bool IsLoopback(string normalizedHostUrl)
    {
        return Uri.TryCreate(normalizedHostUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    /// <summary>
    /// Returns true when the normalized URL's port matches any bound runtime URL.
    /// </summary>
    public static bool PortMatchesAny(string normalizedHostUrl, string[] descriptorUrls)
    {
        if (!Uri.TryCreate(normalizedHostUrl, UriKind.Absolute, out var target))
        {
            return false;
        }

        return descriptorUrls.Any(url =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port == target.Port);
    }

    /// <summary>
    /// Returns same-machine URLs to probe for runtime descriptor liveness.
    /// </summary>
    public static IReadOnlyList<string> SelectLocalProbeUrls(IEnumerable<string> activeUrls)
    {
        var urls = new List<string>();
        foreach (var activeUrl in activeUrls)
        {
            if (!Uri.TryCreate(activeUrl, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var probeUri = TryGetWildcardLoopbackHost(uri.Host, out var loopbackHost)
                ? new UriBuilder(uri) { Host = loopbackHost }.Uri
                : uri;
            var probeUrl = probeUri.ToString().TrimEnd('/');
            if (!urls.Contains(probeUrl, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(probeUrl);
            }
        }

        return urls;
    }

    /// <summary>
    /// Returns a loopback URL suitable for local CLI calls, preferring an active loopback listener.
    /// </summary>
    public static string SelectLocalHost(IEnumerable<string> activeUrls)
    {
        var parsedUrls = activeUrls
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>()
            .ToList();

        var loopback = parsedUrls.FirstOrDefault(uri => uri.IsLoopback);
        if (loopback is not null)
        {
            return loopback.ToString().TrimEnd('/');
        }

        var wildcard = parsedUrls.FirstOrDefault(uri => TryGetWildcardLoopbackHost(uri.Host, out _));
        if (wildcard is not null)
        {
            _ = TryGetWildcardLoopbackHost(wildcard.Host, out var loopbackHost);
            return new UriBuilder(wildcard) { Host = loopbackHost }.Uri.ToString().TrimEnd('/');
        }

        return parsedUrls.FirstOrDefault()?.ToString().TrimEnd('/') ?? DefaultLocalHost;
    }

    private static bool TryGetWildcardLoopbackHost(string host, out string loopbackHost)
    {
        if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            loopbackHost = "127.0.0.1";
            return true;
        }

        if (string.Equals(host, "::", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "[::]", StringComparison.OrdinalIgnoreCase))
        {
            loopbackHost = "::1";
            return true;
        }

        loopbackHost = string.Empty;
        return false;
    }
}
