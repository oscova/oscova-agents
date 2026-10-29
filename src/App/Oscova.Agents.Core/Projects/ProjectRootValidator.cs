using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Core.Projects;

/// <summary>Validates project roots on the device that owns their filesystem.</summary>
public static class ProjectRootValidator
{
    /// <summary>Applies the same private-storage exclusion to every node capability using a project.</summary>
    public static ValidatedProjectRoot ValidateNode(string path, IReadOnlyList<string> privateRoots) =>
        Validate(path, candidate => privateRoots.Any(privateRoot =>
            PathHelper.IsPathWithinDirectory(candidate, privateRoot) || PathHelper.IsPathWithinDirectory(privateRoot, candidate)));

    /// <summary>Validates a local root and applies the executing host's private-storage policy.</summary>
    public static ValidatedProjectRoot Validate(string path, Func<string, bool> isPrivate)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            // No parameter name: the message is shown to the user as is.
            throw new ArgumentException("Project folder must be an absolute path to an existing directory.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var filesystemRoot = Path.GetPathRoot(root)!;
        if (PathHelper.ArePathsEqual(root, filesystemRoot) || !Directory.Exists(root))
        {
            throw new ProjectFolderUnavailableException();
        }

        if (PathHelper.HasSymlinkInPath(root, filesystemRoot))
        {
            throw new UnauthorizedAccessException("Project folder and its ancestors cannot contain symlinks or reparse points.");
        }

        if (isPrivate(root))
        {
            throw new UnauthorizedAccessException("Choose a project folder outside Oscova's private storage.");
        }

        return new(root, PathHelper.GetPathComparisonKey(root));
    }
}
