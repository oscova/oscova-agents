namespace Oscova.Agents.Core.Interfaces;

public static class FileNameValidator
{
    public static bool IsValid(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return !name.Contains("..") && !name.Contains('/') && !name.Contains('\\') &&
               name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    public static void ThrowIfInvalid(string name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException("Invalid file or directory name.");
        }
    }
}
