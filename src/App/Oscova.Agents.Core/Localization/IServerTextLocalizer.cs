namespace Oscova.Agents.Core.Localization;

/// <summary>
/// Resolves localized UI strings for background and non-request code in an explicitly supplied culture.
/// </summary>
public interface IServerTextLocalizer
{
    /// <summary>
    /// Returns the resource value for <paramref name="key"/> in <paramref name="cultureName"/> formatted with <paramref name="args"/>.
    /// </summary>
    string Localize(string cultureName, string key, string fallback, params object[] args);
}
