namespace Oscova.Agents.Tests.TestSupport;

/// <summary>Locates release packages built into the checkout's artifacts/nuget folder for package content tests.</summary>
internal static class ReleasePackages
{
    public static string GetPath(string packageId, string version)
    {
        var directory = Environment.GetEnvironmentVariable("OSCOVA_NUGET_PACKAGE_DIRECTORY")
            ?? Path.Combine(FindRepositoryRoot(), "artifacts", "nuget");
        var path = Path.Combine(directory, $"{packageId}.{version}.nupkg");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Release plugin package was not found: {path}.");
    }

    // The Git root, so packages resolve the same way when this folder is nested in a larger checkout.
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Git repository root.");
    }
}
