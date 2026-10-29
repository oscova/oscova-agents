namespace Oscova.Agents.Infrastructure.Utils;

public static class PathUtil
{
    public const string AppDataDirectoryEnvironmentVariable = "OSCOVA_APP_DATA_DIR";

    public static string AppDataPath
    {
        get
        {
            var configuredPath = Environment.GetEnvironmentVariable(AppDataDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            return Path.Combine(AppContext.BaseDirectory, "app_data");
        }
    }

    /// <summary>Stores shared repository copies used for skill discovery and installation.</summary>
    public static string SkillRegistryCachePath => Path.Combine(AppDataPath, "skill-registry-cache");

    public const string PluginPackageCacheFolderName = "plugin-package-cache";

    /// <summary>Stores NuGet packages restored for plugin installs so later installs reuse them.</summary>
    public static string PluginPackageCachePath => Path.Combine(AppDataPath, PluginPackageCacheFolderName);

    public static string DatabasePath
    {
        get
        {
            return Path.Combine(AppDataPath, "app.db");
        }
    }

    public static string PendingOperationDirectory
    {
        get
        {
            return Path.Combine(AppDataPath, "_pending");
        }
    }

    public static string EnvironmentLockPath
    {
        get
        {
            return Path.Combine(AppDataPath, ".environment.lock");
        }
    }

    public static string RestoreStagingPath
    {
        get
        {
            return Path.Combine(PendingOperationDirectory, "staging");
        }
    }

    public static string RestorePreviousPath
    {
        get
        {
            return Path.Combine(PendingOperationDirectory, "previous");
        }
    }

    public static string RestoreFailedPath
    {
        get
        {
            return Path.Combine(PendingOperationDirectory, "failed");
        }
    }

    public static string ModelsPath
    {
        get
        {
            return Path.Combine(AppDataPath, "models");
        }
    }

    public static string GgufModelsPath
    {
        get
        {
            return Path.Combine(ModelsPath, "gguf");
        }
    }

    public static string VoicesPath
    {
        get
        {
            return Path.Combine(AppDataPath, "voices");
        }
    }

    public static string UsersPath
    {
        get
        {
            return Path.Combine(AppDataPath, "users");
        }
    }

    public static string SharedPath
    {
        get
        {
            return Path.Combine(AppDataPath, "shared");
        }
    }

    public static string HelpDocsPath
    {
        get
        {
            return Path.Combine(AppDataPath, "help");
        }
    }

    public static string PluginsPath
    {
        get
        {
            return Path.Combine(AppDataPath, "plugins");
        }
    }

    public static string SettingsPath
    {
        get
        {
            return Path.Combine(AppDataPath, "settings");
        }
    }

    public static string SystemLogsPath
    {
        get
        {
            return Path.Combine(AppDataPath, "logs", "system");
        }
    }

    public static string SeedProfilePicturesPath
    {
        get
        {
            return Path.Combine(AppDataPath, "seed", "profile-pictures");
        }
    }

    public static string SeedWallpapersPath
    {
        get
        {
            return Path.Combine(AppDataPath, "seed", "wallpapers");
        }
    }
}
