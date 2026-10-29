using Oscova.Agents.Core.Cli;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Infrastructure.Updates;

namespace Oscova.Agents.App.Cli;

public sealed class CliPaths
{
    public CliPaths() : this(null)
    {
    }

    public CliPaths(string? rootDirectory)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(ResolveUserProfileDirectory(), ".oscova")
            : rootDirectory;
    }

    public string RootDirectory { get; }

    /// <summary>Gets the persistent environment directory for a CLI-managed host.</summary>
    public string AppDataDirectory => Path.Combine(RootDirectory, "app_data");

    internal bool UsesDefaultAppDataDirectory { get; private set; }

    public string AuthFilePath => Path.Combine(RootDirectory, "auth.json");
    public string ConfigFilePath => Path.Combine(RootDirectory, "cli-config.yaml");
    public string RuntimeFilePath => Path.Combine(RootDirectory, "runtime.json");
    public string UpdatesDirectory => Path.Combine(RootDirectory, "updates");
    /// <summary>Gets the prompt history file that earlier releases kept in plaintext.</summary>
    public string HistoryFilePath => Path.Combine(RootDirectory, "history");
    public string AvatarDirectory => Path.Combine(RootDirectory, "avatar");
    public string AvatarEngineDirectory => Path.Combine(AvatarDirectory, "engine");
    public string AvatarRuntimeFilePath => Path.Combine(AvatarDirectory, "runtime.json");
    public string AvatarMcpRuntimeFilePath => Path.Combine(AvatarDirectory, "mcp-runtime.json");
    public string AvatarOnboardingMarkerFilePath => Path.Combine(AvatarDirectory, "onboarding-complete");

    public void EnsureRootDirectory()
    {
        CliFileSecurity.EnsurePrivateDirectory(RootDirectory);
    }

    /// <summary>Deletes the plaintext prompt history earlier releases kept; recall now lives only in memory.</summary>
    public void DeletePromptHistory()
    {
        if (File.Exists(HistoryFilePath))
        {
            File.Delete(HistoryFilePath);
        }
    }

    /// <summary>Selects and publishes the environment directory used by every CLI surface.</summary>
    internal void SelectAppDataDirectory()
    {
        var configuredPath = Environment.GetEnvironmentVariable(PathUtil.AppDataDirectoryEnvironmentVariable);
        UsesDefaultAppDataDirectory = string.IsNullOrWhiteSpace(configuredPath);
        var selectedPath = UsesDefaultAppDataDirectory ? AppDataDirectory : configuredPath!;
        Environment.SetEnvironmentVariable(
            PathUtil.AppDataDirectoryEnvironmentVariable,
            Path.GetFullPath(selectedPath));
        Environment.SetEnvironmentVariable(
            ApplicationUpdateFiles.UpdateRootEnvironmentVariable,
            Path.GetFullPath(UpdatesDirectory));
    }

    /// <summary>Refuses local startup when package-owned data still needs a cold transfer.</summary>
    internal void ThrowIfPackageLocalEnvironmentNeedsTransfer()
    {
        if (!UsesDefaultAppDataDirectory)
        {
            return;
        }

        var packageAppDataPath = Path.Combine(AppContext.BaseDirectory, "app_data");
        if (!File.Exists(Path.Combine(packageAppDataPath, "app.db")) &&
            !Directory.Exists(Path.Combine(packageAppDataPath, "users")))
        {
            return;
        }

        throw new CliExitException(
            1,
            "Oscova found existing data inside the installed tool. Stop Oscova, cold-copy " +
            "the complete environment to permanent storage, set OSCOVA_APP_DATA_DIR to " +
            "that copy, and verify it before updating or uninstalling the tool. See the " +
            "Command Line guide.");
    }

    private static string ResolveUserProfileDirectory()
    {
        var primary = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("USERPROFILE")
            : Environment.GetEnvironmentVariable("HOME");

        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        var fallback = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("HOME")
            : Environment.GetEnvironmentVariable("USERPROFILE");

        return string.IsNullOrWhiteSpace(fallback)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : fallback;
    }
}
