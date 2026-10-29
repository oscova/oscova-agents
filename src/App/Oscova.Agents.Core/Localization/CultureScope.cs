using System.Globalization;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.Localization;

/// <summary>
/// Sets the ambient culture for background work and restores the previous culture on dispose.
/// </summary>
public readonly struct CultureScope : IDisposable
{
    private readonly CultureInfo _previousCulture;
    private readonly CultureInfo _previousUiCulture;

    /// <summary>
    /// Sets the current culture and UI culture to the normalized <paramref name="cultureName"/>.
    /// </summary>
    public CultureScope(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(UiCultureCatalog.NormalizeOrDefault(cultureName));
        _previousCulture = CultureInfo.CurrentCulture;
        _previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>
    /// Restores the culture values that were active before the scope was created.
    /// </summary>
    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
    }
}
