namespace Oscova.Agents.Host.Services.UI;

/// <summary>Converts an explicit address-bar submission into a web URL or encoded search.</summary>
public static class BrowserAddress
{
    /// <summary>Resolves HTTP addresses, bare hostnames and search phrases without background requests.</summary>
    public static string Resolve(string value)
    {
        var text = value.Trim();
        if (text.Length is 0 or > 4096) { throw new ArgumentException("Address must contain 1 to 4096 characters."); }

        if (text.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host)) { throw new ArgumentException("Invalid web address."); }

            return url.AbsoluteUri;
        }

        if (!text.Any(char.IsWhiteSpace) && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) &&
            !text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate("https://" + text, UriKind.Absolute, out var host) && !string.IsNullOrEmpty(host.Host) && host.UserInfo.Length == 0)
        { return host.AbsoluteUri; }

        var colon = text.IndexOf(':');
        if (colon > 0 && Uri.CheckSchemeName(text[..colon])) { throw new ArgumentException("Only HTTP and HTTPS addresses are supported."); }

        return "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
    }
}
