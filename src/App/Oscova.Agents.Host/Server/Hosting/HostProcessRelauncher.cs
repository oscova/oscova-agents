using System.Diagnostics;
using System.Reflection;

namespace Oscova.Agents.Host.Server.Hosting;

/// <summary>
/// Starts a replacement host process when a remote CLI or restore flow requests restart.
/// </summary>
public sealed class HostProcessRelauncher(OriginalHostArgs args, ILogger<HostProcessRelauncher> logger)
{
    private const string CliAssemblyName = "Oscova.Agents.App";
    private const string CliCommandName = "oscova";

    /// <summary>
    /// Starts a replacement process for an Oscova CLI-managed host.
    /// </summary>
    public Process StartReplacement(NetworkBindingOverride? bindingOverride = null)
    {
        var startInfo = BuildReplacementStartInfo(
            args.Args,
            Environment.ProcessPath,
            Assembly.GetEntryAssembly()?.Location,
            Environment.CurrentDirectory,
            Environment.ProcessId,
            bindingOverride);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not restart Oscova.");
        logger.LogInformation("Started Oscova replacement process {ProcessId}.", process.Id);
        return process;
    }

    /// <summary>
    /// Builds a replacement process plan only for hosts launched through the Oscova CLI.
    /// </summary>
    public static ProcessStartInfo BuildReplacementStartInfo(
        IReadOnlyList<string> originalArgs,
        string? processPath,
        string? entryAssemblyPath,
        string workingDirectory,
        int waitForPid,
        NetworkBindingOverride? bindingOverride = null)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Current process path is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        if (IsDotNetHost(processPath))
        {
            startInfo.ArgumentList.Add(ResolveCliEntryAssembly(entryAssemblyPath));
        }
        else if (!IsOscovaCliExecutable(processPath))
        {
            throw new InvalidOperationException("Automatic restart is only available when Oscova is launched with the oscova CLI.");
        }

        foreach (var arg in BuildReplacementArgs(originalArgs, waitForPid, bindingOverride))
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    /// <summary>
    /// Builds replacement arguments while removing stale detach and wait markers.
    /// </summary>
    public static IReadOnlyList<string> BuildReplacementArgs(
        IReadOnlyList<string> originalArgs,
        int waitForPid,
        NetworkBindingOverride? bindingOverride = null)
    {
        var replacementArgs = NormalizeRestartArgs(originalArgs, bindingOverride is not null).ToList();
        if (bindingOverride is not null)
        {
            replacementArgs.Add("--host-bind");
            replacementArgs.Add(bindingOverride.Host ?? "localhost");
            replacementArgs.Add("--port");
            replacementArgs.Add(bindingOverride.Port?.ToString() ?? "11317");
        }

        replacementArgs.Add("--wait-for-pid");
        replacementArgs.Add(waitForPid.ToString());
        return replacementArgs;
    }

    /// <summary>Removes process-specific markers before reusing CLI start arguments.</summary>
    public static IReadOnlyList<string> NormalizeRestartArgs(
        IReadOnlyList<string> originalArgs,
        bool removeBinding = false)
    {
        var restartArgs = new List<string>();
        var skipNext = false;

        foreach (var arg in originalArgs)
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (string.Equals(arg, "--detach", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(arg, "--wait-for-pid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--startup-pipe", StringComparison.OrdinalIgnoreCase) ||
                (removeBinding &&
                 (string.Equals(arg, "--host-bind", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase))))
            {
                skipNext = true;
                continue;
            }

            if (arg.StartsWith("--startup-pipe=", StringComparison.OrdinalIgnoreCase)) { continue; }

            if (removeBinding &&
                (arg.StartsWith("--host-bind=", StringComparison.OrdinalIgnoreCase) ||
                 arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            restartArgs.Add(arg);
        }

        if (!restartArgs.Contains("--no-interactive", StringComparer.OrdinalIgnoreCase))
        {
            restartArgs.Add("--no-interactive");
        }

        return restartArgs;
    }

    private static bool IsDotNetHost(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOscovaCliExecutable(string processPath)
    {
        var fileName = Path.GetFileName(processPath);
        // Unix apphosts retain dots in their assembly names and have no executable suffix.
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Path.GetFileNameWithoutExtension(fileName);
        }

        return string.Equals(fileName, CliAssemblyName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, CliCommandName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveCliEntryAssembly(string? entryAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(entryAssemblyPath) || !File.Exists(entryAssemblyPath))
        {
            throw new InvalidOperationException("Current entry assembly is unavailable for restart.");
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(entryAssemblyPath), CliAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Automatic restart is only available when Oscova is launched with the oscova CLI.");
        }

        return entryAssemblyPath;
    }
}

public sealed record OriginalHostArgs(string[] Args);
