using Oscova.Agents.Plugins;

namespace Oscova.Agents.Tests.TestSupport.TestPlugin;

/// <summary>
/// Creates temporary plugin roots backed by the real test plugin build output.
/// </summary>
public sealed class TestPluginRoot : IDisposable
{
    private readonly string _tempDirectory;

    private TestPluginRoot(string tempDirectory)
    {
        _tempDirectory = tempDirectory;
        RootPath = Path.Combine(_tempDirectory, "plugins");
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    /// <summary>
    /// Creates an empty temporary plugin root for a test.
    /// </summary>
    public static TestPluginRoot Create()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "oscova-plugin-tests", Guid.NewGuid().ToString("N"));
        return new TestPluginRoot(tempDirectory);
    }

    /// <summary>
    /// Copies the built test plugin assembly into the root and writes its manifest.
    /// </summary>
    public string AddTestPlugin(string folderName = "oscova.agents.plugins.echo", string? manifestJson = null)
    {
        return CopyTestPluginTo(RootPath, folderName, manifestJson);
    }

    /// <summary>
    /// Copies the built test plugin assembly and its manifest into an existing plugin root.
    /// </summary>
    public static string CopyTestPluginTo(string pluginRoot, string folderName = "oscova.agents.plugins.echo", string? manifestJson = null)
    {
        var pluginDirectory = Path.Combine(pluginRoot, folderName);
        Directory.CreateDirectory(pluginDirectory);

        CopyDirectory(GetTestPluginOutputPath(), pluginDirectory);

        var manifestPath = Path.Combine(pluginDirectory, AgentPluginLoader.ManifestFileName);
        if (manifestJson is null)
        {
            File.Copy(GetTestPluginManifestPath(), manifestPath, overwrite: true);
        }
        else
        {
            File.WriteAllText(manifestPath, manifestJson);
        }

        return pluginDirectory;
    }

    /// <summary>
    /// Copies a built plugin project output into the temporary plugin root.
    /// </summary>
    public string AddPluginFromProjectOutput(
        string projectRelativeDirectory,
        string folderName,
        string manifestRelativePath = AgentPluginLoader.ManifestFileName)
    {
        var pluginDirectory = Path.Combine(RootPath, folderName);
        Directory.CreateDirectory(pluginDirectory);

        var outputPath = GetPluginProjectOutputPath(projectRelativeDirectory);
        CopyDirectory(outputPath, pluginDirectory);

        var manifestPath = Path.Combine(outputPath, manifestRelativePath);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Plugin manifest was not found at '{manifestPath}'.");
        }

        File.Copy(manifestPath, Path.Combine(pluginDirectory, AgentPluginLoader.ManifestFileName), overwrite: true);

        return pluginDirectory;
    }

    /// <summary>Resolves an existing build output whose files remain available for lazy plugin resource loading.</summary>
    public static string GetPluginProjectOutputPath(string projectRelativeDirectory)
    {
        var outputPath = Path.Combine(
            FindRepoRoot(),
            projectRelativeDirectory,
            "bin",
            GetConfigurationName(),
            "net10.0");

        if (!Directory.Exists(outputPath))
        {
            throw new InvalidOperationException($"Plugin output was not found at '{outputPath}'. Build the solution first.");
        }

        return outputPath;
    }

    /// <summary>
    /// Deletes the temporary plugin root.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }

    internal static string GetTestPluginOutputPath()
    {
        var outputPath = Path.Combine(
            FindRepoRoot(),
            "tests",
            "Oscova.Agents.Plugins.Echo",
            "bin",
            GetConfigurationName(),
            "net10.0");

        if (!Directory.Exists(outputPath))
        {
            throw new InvalidOperationException($"Test plugin output was not found at '{outputPath}'. Build the solution first.");
        }

        return outputPath;
    }

    internal static string GetTestPluginManifestPath()
    {
        var manifestPath = Path.Combine(GetTestPluginOutputPath(), AgentPluginLoader.ManifestFileName);

        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Test plugin manifest was not found at '{manifestPath}'.");
        }

        return manifestPath;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var direct = Path.Combine(directory.FullName, "src", "Oscova.Agents");
            if (Directory.Exists(direct))
            {
                return directory.FullName;
            }

            var embedded = Path.Combine(directory.FullName, "src", "oscova-agents");
            if (Directory.Exists(Path.Combine(embedded, "src", "Oscova.Agents")))
            {
                return embedded;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the Oscova.Agents repository root.");
    }

    private static string GetConfigurationName()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
}
