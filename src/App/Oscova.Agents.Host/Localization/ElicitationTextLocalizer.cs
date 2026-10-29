using System.Globalization;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Core.Localization;

namespace Oscova.Agents.Host.Localization;

/// <summary>
/// Resolves ElicitationText resources against the ambient UI culture for text-channel elicitation.
/// </summary>
public sealed class ElicitationTextLocalizer(IStringLocalizer<ElicitationText> localizer) : IElicitationTextLocalizer
{
    /// <inheritdoc />
    public string Localize(string key, string fallback, params object[] args)
    {
        var template = localizer.GetOrDefault(key, fallback);
        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentUICulture, template, args);
    }
}
