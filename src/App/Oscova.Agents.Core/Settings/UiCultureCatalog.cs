using System.Globalization;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Defines the product-owned UI cultures exposed to users and normalizes inbound values.
/// </summary>
public static class UiCultureCatalog
{
    public const string DefaultCultureName = "en-US";

    public static readonly string[] SupportedCultureNames =
    [
        DefaultCultureName,
        "es-ES",
        "zh-CN",
        "hi-IN",
        "ar-SA",
        "fr-FR",
        "bn-BD",
        "pt-BR",
        "id-ID",
        "ur-PK"
    ];

    public static IReadOnlyList<CultureInfo> SupportedCultures { get; } = [.. SupportedCultureNames.Select(CultureInfo.GetCultureInfo)];

    public static bool IsSupported(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return false;
        }

        return SupportedCultureNames.Contains(cultureName.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeOrDefault(string? cultureName)
    {
        if (!IsSupported(cultureName))
        {
            return DefaultCultureName;
        }

        return CultureInfo.GetCultureInfo(cultureName!.Trim()).Name;
    }
}
