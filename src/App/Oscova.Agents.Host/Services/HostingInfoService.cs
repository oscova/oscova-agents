using Microsoft.AspNetCore.Components;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Server.Hosting;
using QRCoder;

namespace Oscova.Agents.Host.Services;

/// <summary>
/// Provides Blazor-friendly host URL helpers and QR rendering for settings screens.
/// </summary>
public sealed class HostingInfoService(NavigationManager navigationManager, NetworkUrlService networkUrlService)
{
    private readonly NavigationManager _navigationManager = navigationManager;
    private readonly NetworkUrlService _networkUrlService = networkUrlService;

    /// <summary>
    /// Returns the current browser-facing base URL for this Blazor circuit.
    /// </summary>
    public string GetLocalHostUrl()
    {
        var baseUri = _navigationManager.BaseUri;
        return baseUri.TrimEnd('/');
    }

    /// <summary>
    /// Returns a LAN URL that uses the current browser scheme and port.
    /// </summary>
    public string? GetNetworkUrl()
    {
        var baseUri = new Uri(_navigationManager.BaseUri);
        return _networkUrlService.GetNetworkUrl(baseUri.Port, baseUri.Scheme);
    }

    /// <summary>
    /// Builds a localhost URL for a configured port.
    /// </summary>
    public string GetLocalHostUrl(int port, string scheme = "http")
    {
        return _networkUrlService.GetLocalHostUrl(port, scheme);
    }

    /// <summary>
    /// Builds a LAN URL for a configured port.
    /// </summary>
    public string? GetNetworkUrl(int port, string scheme = "http")
    {
        return _networkUrlService.GetNetworkUrl(port, scheme);
    }

    /// <summary>
    /// Resolves the public app URL from saved network settings.
    /// </summary>
    public string? GetEffectivePublicAppUrl(NetworkSettings.Snapshot settings)
    {
        return _networkUrlService.GetEffectivePublicAppUrl(settings);
    }

    /// <summary>
    /// Generates a PNG QR code as base64 for the supplied URL.
    /// </summary>
    public string GenerateQrCodeBase64(string url)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrCodeData);
        
        byte[] qrCodeImage = qrCode.GetGraphic(20);
        return Convert.ToBase64String(qrCodeImage);
    }
}
