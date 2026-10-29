namespace Oscova.Agents.Host.Utils;

/// <summary>Links public App versions to their release announcements.</summary>
public static class ReleaseNotesLinks
{
    public const string History = "https://github.com/oscova/oscova-agents/releases";

    /// <summary>Returns an exact announcement only for versions eligible for public publication.</summary>
    public static string? ForVersion(string? version)
    {
        // Validate raw metadata first, normalization would hide private four-part versions.
        if (!Version.TryParse(version, out var parsed) || parsed.Major < 2 || parsed.Build < 0
            || parsed.Revision >= 0 || !string.Equals(version, parsed.ToString(3), StringComparison.Ordinal))
        {
            return null;
        }

        return $"{History}/tag/v{version}";
    }
}
