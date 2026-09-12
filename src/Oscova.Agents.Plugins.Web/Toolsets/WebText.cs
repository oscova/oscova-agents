namespace Oscova.Agents.Plugins.Web.Toolsets;

/// <summary>
/// Shared text shaping for web toolsets so search and news results stay small enough
/// for small local context windows.
/// </summary>
internal static class WebText
{
    /// <summary>
    /// Flattens whitespace to a single line and caps the length, appending an ellipsis when trimmed.
    /// </summary>
    public static string ToSingleLine(string value, int maxLength)
    {
        var flattened = string.Join(
            " ",
            value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (flattened.Length <= maxLength)
        {
            return flattened;
        }

        return $"{flattened[..(maxLength - 3)].TrimEnd()}...";
    }
}
