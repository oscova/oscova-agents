using Oscova.Agents.Core.Settings;
using System.Net;
using System.Net.Sockets;

namespace Oscova.Agents.Host.Server.Hosting;

/// <summary>
/// Builds local and LAN-reachable URLs for this web host without depending on Blazor services.
/// </summary>
public sealed class NetworkUrlService(WebHostRuntimeState runtimeState)
{
    /// <summary>
    /// Returns true when the active listener URLs include an all-interface host binding.
    /// </summary>
    public bool IsNetworkAccessActive(IEnumerable<string> activeUrls)
    {
        return activeUrls.Any(IsAllInterfacesUrl);
    }

    /// <summary>
    /// Returns the first parseable HTTP/HTTPS port from active server URLs.
    /// </summary>
    public int? GetPrimaryActivePort(IEnumerable<string> activeUrls)
    {
        foreach (var activeUrl in activeUrls)
        {
            if (TryCreateBindingUri(activeUrl, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.Port;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the port the host listens on, which a launch override can make differ from the saved port.
    /// </summary>
    public int GetListenerPort(NetworkSettings.Snapshot settings)
    {
        return GetPrimaryActivePort(runtimeState.ActiveUrls) ?? settings.Port;
    }

    /// <summary>
    /// Checks whether saved bind intent differs from the current listener state.
    /// </summary>
    public bool RequiresRestart(
        NetworkSettings.Snapshot settings,
        IEnumerable<string> activeUrls)
    {
        var activeUrlList = activeUrls.ToArray();
        return IsNetworkAccessActive(activeUrlList) != settings.EnableNetworkAccess ||
               GetPrimaryActivePort(activeUrlList) != settings.Port;
    }

    /// <summary>
    /// Builds a localhost URL for a configured port.
    /// </summary>
    public string GetLocalHostUrl(int port, string scheme = "http")
    {
        return FormatUrl(scheme, "localhost", NetworkSettings.NormalizePort(port));
    }

    /// <summary>
    /// Returns the HTTP listener URL this host can call itself on, or null before startup records one.
    /// </summary>
    public string? GetSelfCallUrl()
    {
        foreach (var activeUrl in runtimeState.ActiveUrls)
        {
            if (TryCreateBindingUri(activeUrl, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
            {
                return IsAllInterfacesUrl(activeUrl) ? GetLocalHostUrl(uri.Port) : uri.GetLeftPart(UriPartial.Authority);
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a LAN URL from the active outbound IPv4 interface, if one can be detected.
    /// </summary>
    public string? GetNetworkUrl(int port, string scheme = "http")
    {
        var localIp = GetNetworkIpAddress();
        return localIp is null ? null : FormatUrl(scheme, localIp, NetworkSettings.NormalizePort(port));
    }

    /// <summary>
    /// Resolves the URL external integrations should use for this application.
    /// </summary>
    public string? GetEffectivePublicAppUrl(NetworkSettings.Snapshot settings)
    {
        if (settings.PublicAppUrlMode == PublicAppUrlMode.Custom)
        {
            return string.IsNullOrWhiteSpace(settings.CustomPublicAppUrl)
                ? null
                : settings.CustomPublicAppUrl.Trim().TrimEnd('/');
        }

        if (!settings.EnableNetworkAccess)
        {
            return null;
        }

        return GetNetworkUrl(GetListenerPort(settings));
    }

    /// <summary>
    /// Detects the active outbound IPv4 address using UDP routing without sending payload data.
    /// </summary>
    public string? GetNetworkIpAddress()
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Connect("8.8.8.8", 65530);
            if (udpClient.Client.LocalEndPoint is IPEndPoint localEndPoint &&
                localEndPoint.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                return localEndPoint.Address.ToString();
            }
        }
        catch
        {
            // Network detection is best-effort; callers surface a missing URL as unavailable.
        }

        return null;
    }

    private static string FormatUrl(string scheme, string host, int port)
    {
        var normalizedScheme = string.IsNullOrWhiteSpace(scheme)
            ? Uri.UriSchemeHttp
            : scheme.Trim().TrimEnd(':', '/', '\\').ToLowerInvariant();

        if ((normalizedScheme == Uri.UriSchemeHttp && port == 80) ||
            (normalizedScheme == Uri.UriSchemeHttps && port == 443))
        {
            return $"{normalizedScheme}://{host}";
        }

        return $"{normalizedScheme}://{host}:{port}";
    }

    private static bool IsAllInterfacesUrl(string url)
    {
        var normalized = url.Trim();
        if (normalized.Contains("://*:", StringComparison.Ordinal) ||
            normalized.Contains("://+:", StringComparison.Ordinal))
        {
            return true;
        }

        if (!TryCreateBindingUri(url, out var uri))
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        return host is "0.0.0.0" or "*" or "+" or "::";
    }

    private static bool TryCreateBindingUri(string value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        normalized = normalized.Replace("://*:", "://localhost:", StringComparison.Ordinal)
            .Replace("://+:", "://localhost:", StringComparison.Ordinal);

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var parsedUri) &&
            parsedUri is not null)
        {
            uri = parsedUri;
            return true;
        }

        return false;
    }
}
