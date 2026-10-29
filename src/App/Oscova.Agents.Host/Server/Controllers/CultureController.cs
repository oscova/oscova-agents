using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Synchronizes the authenticated user's selected UI culture into the standard ASP.NET Core localization cookie.
/// </summary>
[Authorize]
[Route("[controller]/[action]")]
public sealed class CultureController : Controller
{
    [HttpGet]
    public IActionResult Set(string culture, string redirectUri)
    {
        var normalizedCulture = UiCultureCatalog.NormalizeOrDefault(culture);
        var safeRedirectUri = Url.IsLocalUrl(redirectUri) ? redirectUri : "/";

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(
                new RequestCulture(normalizedCulture, normalizedCulture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                Path = "/"
            });

        return LocalRedirect(safeRedirectUri);
    }
}