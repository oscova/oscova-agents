namespace Oscova.Agents.Core.Localization;

/// <summary>
/// Resolves elicitation strings in the ambient UI culture set by the caller.
/// </summary>
public interface IElicitationTextLocalizer
{
    /// <summary>
    /// Returns the resource value for <paramref name="key"/> formatted with <paramref name="args"/>.
    /// </summary>
    string Localize(string key, string fallback, params object[] args);
}
