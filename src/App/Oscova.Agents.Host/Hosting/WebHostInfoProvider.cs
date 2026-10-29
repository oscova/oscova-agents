using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Server.Hosting;

namespace Oscova.Agents.Host.Hosting;

/// <summary>
/// Default <see cref="IWebHostInfoProvider"/> backed by the live ASP.NET Core host
/// and the saved <see cref="NetworkSettings"/>.
/// </summary>
public sealed class WebHostInfoProvider(
    IWebHostEnvironment env,
    NetworkSettings networkSettings,
    NetworkUrlService networkUrlService,
    ILogger<WebHostInfoProvider> logger) : IWebHostInfoProvider
{
    private int _localhostFallbackWarned;

    public string? WebRootPath => env.WebRootPath;

    public string? BuildAbsoluteUrl(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        // Pass-through for already-absolute URLs (defensive: callers send /tts/... today).
        if (Uri.TryCreate(relativePath, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        var snapshot = networkSettings.CreateSnapshot();
        var baseUrl = networkUrlService.GetEffectivePublicAppUrl(snapshot);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = networkUrlService.GetLocalHostUrl(networkUrlService.GetListenerPort(snapshot));

            // Log once per process so single-machine setups don't flood logs.
            if (Interlocked.CompareExchange(ref _localhostFallbackWarned, 1, 0) == 0)
            {
                logger.LogInformation(
                    "Public app URL is not configured; falling back to {Fallback} for absolute URL building. " +
                    "Enable network access or set a custom public app URL if the avatar engine runs on another machine.",
                    baseUrl);
            }
        }

        var trimmedBase = baseUrl.TrimEnd('/');
        var path = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        return trimmedBase + path;
    }
}
