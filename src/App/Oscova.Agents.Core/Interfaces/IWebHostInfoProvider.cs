namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// Bridges hosting-only details (web root path, public app URL composition) into
/// projects that must not depend on the Web host directly.
/// </summary>
public interface IWebHostInfoProvider
{
    /// <summary>
    /// Absolute filesystem path to the web root (wwwroot), or null when not hosted.
    /// </summary>
    string? WebRootPath { get; }

    /// <summary>
    /// Resolves an absolute URL for a relative app path (e.g. <c>/tts/anon/file.wav</c>)
    /// using the configured public app URL, with a localhost fallback when no
    /// network-reachable URL is configured.
    /// </summary>
    /// <returns>The absolute URL, or null when the input is null or empty.</returns>
    string? BuildAbsoluteUrl(string? relativePath);
}
