using System.Globalization;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Host.Localization;

/// <summary>
/// Resolves CommonText resources in an explicit culture for background callers.
/// </summary>
public sealed class ServerTextLocalizer(IStringLocalizer<CommonText> localizer) : IServerTextLocalizer
{
    public string Localize(string cultureName, string key, string fallback, params object[] args)
    {
        var culture = CultureInfo.GetCultureInfo(UiCultureCatalog.NormalizeOrDefault(cultureName));
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            var template = localizer.GetOrDefault(key, fallback);
            return args.Length == 0 ? template : string.Format(culture, template, args);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
