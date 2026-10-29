using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Xml;
using NuGet.Versioning;
using Oscova.Agents.Core.Updates;
using Oscova.Agents.Host.Server.Hosting;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Utils;

namespace Oscova.Agents.App.Cli;

/// <summary>Verifies the running global tool and resolves its installed command.</summary>
public sealed class ApplicationToolInstallation(CliPaths paths)
{
    private const int MaxProcessOutput = 1024 * 1024;

    /// <summary>Gets the NuGet package version embedded during packing.</summary>
    public static string? GetAssemblyPackageVersion() =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "OscovaToolPackageVersion", StringComparison.Ordinal))
            ?.Value;

    /// <summary>Returns the verified current global-tool identity or null for unsupported launch modes.</summary>
    public async Task<ApplicationToolIdentity?> TryResolveCurrentAsync(
        IReadOnlyList<string> originalArguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var packageVersion = GetAssemblyPackageVersion();
            if (!NuGetVersion.TryParse(packageVersion, out _))
            {
                return null;
            }

            var dotnetPath = ResolveDotnetPath();
            await VerifySdkAsync(dotnetPath, cancellationToken).ConfigureAwait(false);
            var globalToolsPath = ResolveGlobalToolsPath();
            var installedVersion = await GetInstalledVersionAsync(dotnetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(installedVersion, packageVersion, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var storeVersionRoot = GetStoreVersionRoot(globalToolsPath, packageVersion!);
            var entryPath = Path.GetFullPath(Assembly.GetEntryAssembly()!.Location);
            if (!PathHelper.IsPathWithinDirectory(entryPath, storeVersionRoot) || HasReparseAncestor(entryPath, storeVersionRoot))
            {
                return null;
            }

            var appDataPath = Path.GetFullPath(Infrastructure.Utils.PathUtil.AppDataPath);
            var updatesDirectory = Path.GetFullPath(paths.UpdatesDirectory);
            var storeRoot = Path.Combine(globalToolsPath, ".store");
            if (PathHelper.IsPathWithinDirectory(appDataPath, storeRoot) ||
                PathHelper.IsPathWithinDirectory(updatesDirectory, storeRoot))
            {
                return null;
            }

            var workingDirectory = Path.GetFullPath(Environment.CurrentDirectory);
            if (PathHelper.IsPathWithinDirectory(workingDirectory, storeRoot))
            {
                workingDirectory = appDataPath;
            }

            return new ApplicationToolIdentity(
                packageVersion!,
                dotnetPath,
                globalToolsPath,
                appDataPath,
                updatesDirectory,
                workingDirectory,
                BuildRestartArguments(originalArguments));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Confirms the exact global-tool version installed by the SDK.</summary>
    public static Task<string?> GetInstalledVersionAsync(string dotnetPath, CancellationToken cancellationToken) =>
        GetInstalledVersionCoreAsync(dotnetPath, cancellationToken);

    /// <summary>Resolves the installed command from its trusted tool settings.</summary>
    public static string ResolveInstalledCommand(string globalToolsPath, string version)
    {
        var versionRoot = GetStoreVersionRoot(globalToolsPath, version);
        if (!Directory.Exists(versionRoot) || HasReparseAncestor(versionRoot, Path.Combine(globalToolsPath, ".store")))
        {
            throw new InvalidOperationException("The installed application package cannot be resolved safely.");
        }

        return ResolveSingleNativeCommand(versionRoot);
    }

    /// <summary>
    /// Copies the installed version into the updates folder and returns the copied application DLL.
    /// The update runs that copy through the dotnet muxer, because the tool store is replaced underneath it
    /// and macOS Gatekeeper rejects the unsigned native command outside the tool store.
    /// </summary>
    public static string PrepareUpdaterCopy(string globalToolsPath, string version, string updatesDirectory)
    {
        var source = Path.GetDirectoryName(ResolveInstalledCommand(globalToolsPath, version))!;
        var copy = Path.Combine(updatesDirectory, "updater");
        try
        {
            if (Directory.Exists(copy))
            {
                Directory.Delete(copy, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The previous update copy at {copy} is still in use. Close the previous update window and try again.", ex);
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(copy, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return Path.Combine(copy, typeof(ApplicationToolInstallation).Assembly.GetName().Name + ".dll");
    }

    private static string ResolveSingleNativeCommand(string root)
    {
        if (HasReparseAncestor(root, root))
        {
            throw new InvalidOperationException("The application package cannot contain linked command paths.");
        }

        var matches = new List<string>();
        foreach (var settingsPath in Directory.EnumerateFiles(root, "DotnetToolSettings.xml", SearchOption.AllDirectories))
        {
            if (HasReparseAncestor(settingsPath, root))
            {
                throw new InvalidOperationException("The application package cannot contain linked command paths.");
            }

            var settingsDirectory = Path.GetDirectoryName(settingsPath)!;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(settingsPath, settings);
            var document = new XmlDocument { XmlResolver = null };
            document.Load(reader);
            var command = document.SelectSingleNode("/DotNetCliTool/Commands/Command") as XmlElement;
            if (command is null ||
                !string.Equals(command.GetAttribute("Name"), "oscova", StringComparison.Ordinal) ||
                !string.Equals(command.GetAttribute("Runner"), "executable", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = Path.GetFullPath(Path.Combine(settingsDirectory, command.GetAttribute("EntryPoint")));
            if (PathHelper.IsPathWithinDirectory(candidate, root) &&
                File.Exists(candidate) &&
                !HasReparseAncestor(candidate, root))
            {
                matches.Add(candidate);
            }
        }

        return matches.Distinct(PathComparer).SingleOrDefault()
            ?? throw new InvalidOperationException("The installed application does not contain one supported native command.");
    }

    internal static async Task<string> RunDotnetAsync(
        string dotnetPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The .NET SDK could not be started.");
        var outputTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var errorTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (output.Count + error.Count > MaxProcessOutput)
        {
            throw new InvalidOperationException("The .NET SDK returned too much output.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error.Text) ? "The .NET SDK command failed." : error.Text.Trim());
        }

        return output.Text;
    }

    /// <summary>Drains process output while retaining at most the configured diagnostic limit.</summary>
    private static async Task<(string Text, long Count)> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var text = new System.Text.StringBuilder(MaxProcessOutput);
        long count = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            count += read;
            var remaining = MaxProcessOutput - text.Length;
            if (remaining > 0)
            {
                text.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return (text.ToString(), count);
    }

    private static async Task VerifySdkAsync(string dotnetPath, CancellationToken cancellationToken)
    {
        var sdkVersion = (await RunDotnetAsync(dotnetPath, ["--version"], cancellationToken).ConfigureAwait(false)).Trim();
        if (!Version.TryParse(sdkVersion.Split('-', 2)[0], out var parsed) || parsed.Major < 10)
        {
            throw new InvalidOperationException("Application updates require the .NET 10 SDK or newer.");
        }

        var runtimes = await RunDotnetAsync(dotnetPath, ["--list-runtimes"], cancellationToken).ConfigureAwait(false);
        if (!runtimes.Split('\n').Any(line => line.TrimStart().StartsWith("Microsoft.NETCore.App 10.", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Application updates require the .NET 10 runtime.");
        }
    }

    private static async Task<string?> GetInstalledVersionCoreAsync(string dotnetPath, CancellationToken cancellationToken)
    {
        var output = await RunDotnetAsync(
            dotnetPath,
            ["tool", "list", NuGetApplicationUpdateSource.PackageId, "--global", "--format", "json"],
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("version", out var formatVersion) ||
            !formatVersion.TryGetInt32(out var contractVersion) || contractVersion != 1 ||
            !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The installed-tool response is not supported.");
        }

        var matches = data.EnumerateArray().Where(item =>
            item.TryGetProperty("packageId", out var id) && id.ValueKind == JsonValueKind.String &&
            string.Equals(id.GetString(), NuGetApplicationUpdateSource.PackageId, StringComparison.OrdinalIgnoreCase) &&
            item.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array &&
            commands.GetArrayLength() == 1 &&
            commands[0].ValueKind == JsonValueKind.String &&
            string.Equals(commands[0].GetString(), "oscova", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return version.GetString();
    }

    internal static string ResolveDotnetPath()
    {
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.GetFullPath(Path.Combine(directory.Trim().Trim('"'), executableName));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The .NET SDK executable was not found on PATH.");
    }

    internal static string ResolveGlobalToolsPath()
    {
        var home = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.GetFullPath(Path.Combine(home, ".dotnet", "tools"));
    }

    private static string GetStoreVersionRoot(string globalToolsPath, string version)
    {
        if (!NuGetVersion.TryParse(version, out _))
        {
            throw new InvalidOperationException("The application package version is invalid.");
        }

        return Path.GetFullPath(Path.Combine(
            globalToolsPath,
            ".store",
            NuGetApplicationUpdateSource.PackageId.ToLowerInvariant(),
            version));
    }

    private static IReadOnlyList<string> BuildRestartArguments(IReadOnlyList<string> arguments)
    {
        var result = HostProcessRelauncher.NormalizeRestartArgs(arguments).ToList();

        if (result.Count == 0 || !string.Equals(result[0], "start", StringComparison.OrdinalIgnoreCase))
        {
            result.Insert(0, "start");
        }

        return result;
    }

    private static bool HasReparseAncestor(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetFullPath(root);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        var current = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new FileInfo(fullPath).Directory;
        while (current is not null && (PathHelper.ArePathsEqual(current.FullName, rootPath) || PathHelper.IsPathWithinDirectory(current.FullName, rootPath)))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
