using Oscova.Agents.Core.Cli;
using Oscova.Agents.Persistence;

namespace Oscova.Agents.App.Cli;

public sealed class CliConfigStore(CliPaths paths)
{
    private readonly YamlConverter _yaml = new();

    public CliConfigFile Read()
    {
        if (!File.Exists(paths.ConfigFilePath))
        {
            return new CliConfigFile();
        }

        var content = File.ReadAllText(paths.ConfigFilePath);
        return string.IsNullOrWhiteSpace(content) ? new CliConfigFile() : _yaml.Deserialize<CliConfigFile>(content);
    }

    public void Write(CliConfigFile config)
    {
        paths.EnsureRootDirectory();
        var tempPath = paths.ConfigFilePath + ".tmp";
        File.WriteAllText(tempPath, _yaml.Serialize(config));
        File.Move(tempPath, paths.ConfigFilePath, overwrite: true);
    }

    /// <summary>
    /// Saves the default host used by client commands when no explicit host is provided.
    /// </summary>
    public bool SetHost(string host)
    {
        var normalizedHost = CliHostResolver.NormalizeHostUrl(host);
        var config = Read();
        if (string.Equals(config.Host, normalizedHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        config.Host = normalizedHost;
        Write(config);
        return true;
    }
}
