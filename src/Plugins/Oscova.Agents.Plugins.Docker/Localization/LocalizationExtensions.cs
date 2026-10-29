using Microsoft.Extensions.Localization;
namespace Oscova.Agents.Plugins.Docker.Localization;
/// <summary>Retains readable defaults when a translation is unavailable.</summary>
public static class LocalizationExtensions
{
    public static string GetOrDefault(this IStringLocalizer localizer, string key, string fallback)
    { var value = localizer[key]; return value.ResourceNotFound ? fallback : value.Value; }
}
