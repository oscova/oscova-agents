namespace Oscova.Agents.Core.Users;

/// <summary>
/// Defines the text file types users can create, edit, and back up through the file browser.
/// </summary>
public static class UserEditableFileTypes
{
    private static readonly string[] ExtensionValues =
        [".md", ".txt", ".json", ".yaml", ".yml", ".csv", ".xml", ".enc"];

    private static readonly HashSet<string> ExtensionSet = new(ExtensionValues, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the editable file extensions in display order.
    /// </summary>
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(ExtensionValues);

    /// <summary>
    /// Gets the editable file extensions formatted for user-facing hints.
    /// </summary>
    public static string DisplayList => string.Join(", ", ExtensionValues);

    /// <summary>
    /// Returns whether an extension is one of the supported editable text types.
    /// </summary>
    public static bool ContainsExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return ExtensionSet.Contains(extension);
    }

    /// <summary>
    /// Returns whether a file name has one of the supported editable text extensions.
    /// </summary>
    public static bool ContainsFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fileName)))
        {
            return false;
        }

        return ContainsExtension(Path.GetExtension(fileName));
    }
}
