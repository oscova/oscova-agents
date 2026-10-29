using System.Net;
using System.Net.Sockets;

namespace Oscova.Agents.Infrastructure.Net;

/// <summary>
/// Decides whether an already-resolved IP is safe for web__fetch to connect to.
/// </summary>
public static class SsrfAddressValidator
{
    /// <summary>
    /// Returns true only for public IP addresses that cannot target the host, LAN, or metadata endpoints.
    /// </summary>
    public static bool IsPublic(IPAddress address) =>
        Classify(address) == AddressClassification.Public;

    /// <summary>
    /// Returns true when web__fetch may connect to the address under the current private-network setting.
    /// </summary>
    public static bool IsAllowedForWebFetch(IPAddress address, bool allowPrivateNetwork)
    {
        var classification = Classify(address);
        return classification == AddressClassification.Public ||
               allowPrivateNetwork && classification == AddressClassification.PrivateOrLoopback;
    }

    private static AddressClassification Classify(IPAddress address)
    {
        // IPv4-mapped IPv6 addresses must be judged by their real IPv4 bytes.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        // Loopback is the host talking to itself.
        if (IPAddress.IsLoopback(address))
        {
            return AddressClassification.PrivateOrLoopback;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0) return AddressClassification.Blocked;         // this network
            if (bytes[0] == 10) return AddressClassification.PrivateOrLoopback; // private LAN
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return AddressClassification.PrivateOrLoopback; // private LAN
            if (bytes[0] == 192 && bytes[1] == 168) return AddressClassification.PrivateOrLoopback; // private LAN
            if (bytes[0] == 169 && bytes[1] == 254) return AddressClassification.Blocked; // link-local and metadata
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return AddressClassification.Blocked; // carrier-grade NAT
            if (bytes[0] >= 224) return AddressClassification.Blocked;       // multicast and reserved
            return AddressClassification.Public;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any)) return AddressClassification.Blocked; // unspecified

            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return AddressClassification.PrivateOrLoopback; // unique-local
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return AddressClassification.Blocked; // link-local
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0) return AddressClassification.Blocked; // site-local (deprecated)
            if (bytes[0] == 0xFF) return AddressClassification.Blocked;     // multicast
            return AddressClassification.Public;
        }

        return AddressClassification.Blocked;
    }

    private enum AddressClassification
    {
        Public,
        PrivateOrLoopback,
        Blocked
    }
}
