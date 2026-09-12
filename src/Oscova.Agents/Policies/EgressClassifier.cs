using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Policies;

/// <summary>
/// Classifies provider endpoints and arbitrary URIs as local/LAN or public-cloud egress.
/// </summary>
public static class EgressClassifier
{
    /// <summary>
    /// Returns true when the URI host resolves only to loopback, RFC 1918, link-local, or unique-local addresses.
    /// </summary>
    public static bool IsLocalHost(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        try
        {
            if (uri.IsLoopback)
            {
                return true;
            }

            if (IPAddress.TryParse(uri.Host, out var address))
            {
                return IsLocalAddress(address);
            }

            var addresses = Dns.GetHostAddresses(uri.Host);
            return addresses.Length > 0 && addresses.All(IsLocalAddress);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Classifies a model provider by resolving its effective endpoint.
    /// </summary>
    public static EgressClass Classify(
        string type,
        string? baseUrl,
        string? defaultEndpoint,
        bool? isLocalEndpoint = null,
        bool? defaultIsLocalEndpoint = null)
    {
        if (isLocalEndpoint is bool local)
        {
            return local ? EgressClass.Local : EgressClass.Cloud;
        }

        if (defaultIsLocalEndpoint is bool providerLocal)
        {
            return providerLocal ? EgressClass.Local : EgressClass.Cloud;
        }

        if (type == ModelProviderKeys.Local)
        {
            return EgressClass.Local;
        }

        return ResolveEndpoint(baseUrl, defaultEndpoint) is { } uri && IsLocalHost(uri)
            ? EgressClass.Local
            : EgressClass.Cloud;
    }

    /// <summary>
    /// Classifies a speech provider by resolving its effective endpoint.
    /// </summary>
    public static EgressClass ClassifySpeech(string? baseUrl, string? defaultEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var configuredUri) && IsLocalHost(configuredUri)
                ? EgressClass.Local
                : EgressClass.Cloud;
        }

        if (string.IsNullOrWhiteSpace(defaultEndpoint))
        {
            return EgressClass.Local;
        }

        return Uri.TryCreate(defaultEndpoint, UriKind.Absolute, out var defaultUri) && IsLocalHost(defaultUri)
            ? EgressClass.Local
            : EgressClass.Cloud;
    }

    private static Uri? ResolveEndpoint(string? baseUrl, string? defaultEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var configuredUri))
        {
            return configuredUri;
        }

        return !string.IsNullOrWhiteSpace(defaultEndpoint) &&
            Uri.TryCreate(defaultEndpoint, UriKind.Absolute, out var defaultUri)
                ? defaultUri
                : null;
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6Loopback) ||
                   bytes[0] is >= 0xfc and <= 0xfd ||
                   bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80;
        }

        return false;
    }
}
