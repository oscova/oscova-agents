using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;

namespace Oscova.Agents.Host.Utils;

/// <summary>Keeps the requested local page across the browser sign-in flow.</summary>
internal static class LoginNavigation
{
    /// <summary>Reads the login return URL or the protected page showing the sign-in panel.</summary>
    internal static string GetReturnUrl(NavigationManager navigation)
    {
        var uri = navigation.ToAbsoluteUri(navigation.Uri);
        if (IsLoginRoute(navigation))
        {
            var query = QueryHelpers.ParseQuery(uri.Query);
            return LocalReturnUrl(query.TryGetValue("returnUrl", out var value) ? value.ToString() : null);
        }

        return LocalReturnUrl(uri.PathAndQuery + uri.Fragment);
    }

    /// <summary>Recognizes login pages independently of their return URL and error query parameters.</summary>
    internal static bool IsLoginRoute(NavigationManager navigation)
    {
        var path = navigation.ToAbsoluteUri(navigation.Uri).GetLeftPart(UriPartial.Path);
        var route = navigation.ToBaseRelativePath(path).Trim('/');
        return string.Equals(route, "login", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(route, "login/admin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rejects external destinations using the same rule as local HTTP redirects.</summary>
    internal static string LocalReturnUrl(string? url) => RedirectHttpResult.IsLocalUrl(url) ? url : "/";
}
