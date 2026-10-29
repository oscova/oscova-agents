using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NuGet.Versioning;
using Oscova.Agents.Core.Backup;
using Oscova.Agents.Host.Hosting;
using Oscova.Agents.Infrastructure.Backup;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Updates;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Utils;

namespace Oscova.Agents.App.Cli;

/// <summary>Validates owned data and removes it only after confirmed local shutdown.</summary>
public sealed class EnvironmentEraseOperation(CliPaths paths, LocalHostControl localControl, AvatarEngineManager avatarEngine)
{
    /// <summary>Resolves the exact owned targets without creating files or reading credentials.</summary>
    public IReadOnlyList<EraseTarget> Preview()
    {
        var root = Path.GetFullPath(paths.RootDirectory);
        var appData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PathUtil.AppDataPath));
        var targets = new List<EraseTarget> { new(root, false) };
        if (!PathHelper.IsPathWithinDirectory(appData, root))
        {
            if (Directory.Exists(appData)) { targets.Add(new(appData, true)); }

            var parent = Directory.GetParent(appData)?.FullName;
            if (parent is not null && Directory.Exists(parent))
            {
                ValidatePath(parent, inspectChildren: false, rejectProtectedRoots: false);
                var prefix = Path.GetFileName(appData) + ".reset-";
                foreach (var sibling in Directory.EnumerateDirectories(parent, prefix + "*"))
                {
                    var suffix = Path.GetFileName(sibling)[prefix.Length..];
                    if (suffix.Length != 48 || suffix[15] != '-' ||
                        !DateTimeOffset.TryParseExact(suffix[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created) ||
                        !Guid.TryParseExact(suffix[16..], "N", out _)) { continue; }

                    var expected = EnvironmentRestoreBootstrapper.GetResetBackupPath(new PendingEnvironmentOperation
                    {
                        Mode = EnvironmentOperationMode.Reset, ResetCreatedAtUtc = created, ResetOperationId = suffix[16..]
                    });
                    if (PathHelper.ArePathsEqual(sibling, expected)) { targets.Add(new(sibling, true)); }
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            targets.Add(new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OscovaAvatarEngine"), false));
        }

        var distinct = targets.Where(target => !targets.Any(other =>
            !ReferenceEquals(target, other) && !PathHelper.ArePathsEqual(target.Path, other.Path) &&
            PathHelper.IsPathWithinDirectory(target.Path, other.Path))).ToArray();
        ValidateTargets(distinct);
        return distinct;
    }

    /// <summary>Stops owned processes and erases the validated targets while the caller holds lifecycle ownership.</summary>
    public async Task<IReadOnlyList<string>> StopAndEraseAsync(IReadOnlyList<EraseTarget> confirmed, CancellationToken ct)
    {
        ValidateTargets(confirmed);
        var current = Preview();
        if (current.Count != confirmed.Count || current.Any(target => !confirmed.Any(item =>
            item.RequiresDescriptor == target.RequiresDescriptor && PathHelper.ArePathsEqual(item.Path, target.Path))))
        {
            throw new CliExitException(1, "Oscova's erase targets changed. Run the command again to review and confirm them.");
        }

        var descriptor = ResolveSelectedHost();
        using var updateLease = ApplicationUpdateFiles.AcquireUpdateLease();
        await localControl.StopLocalServicesAsync(descriptor, ct).ConfigureAwait(false);
        if (OperatingSystem.IsWindows()) { await avatarEngine.StopAsync(ct).ConfigureAwait(false); }

        using var environmentLease = Directory.Exists(PathUtil.AppDataPath)
            ? EnvironmentRestoreBootstrapper.AcquireEnvironmentLease() : null;
        ValidateTargets(confirmed);
        ct.ThrowIfCancellationRequested();
        environmentLease?.Dispose();
        updateLease.Dispose();

        var removed = new List<string>();
        try
        {
            foreach (var target in confirmed.Where(target => Directory.Exists(target.Path)))
            {
                // The external gate remains held while in-target file handles are released for deletion.
                ValidatePath(target.Path);
                if (target.RequiresDescriptor) { DeleteExternalEnvironment(target.Path); }
                else { Directory.Delete(target.Path, recursive: true); }

                removed.Add(target.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CliExitException)
        {
            var remaining = confirmed.Where(target => Directory.Exists(target.Path)).Select(target => target.Path);
            throw new CliExitException(1, $"Erasure is incomplete: {ex.Message} Remaining targets: {string.Join(", ", remaining)}. No new Host was started.");
        }

        return removed;
    }

    private static RuntimeDescriptor? ResolveSelectedHost()
    {
        var descriptor = RuntimeDescriptorWriter.ReadOrNull();
        if (descriptor is null)
        {
            if (File.Exists(RuntimeDescriptorWriter.RuntimePath))
            {
                throw new CliExitException(1, "The local Host descriptor is invalid. Resolve its process identity before erasing.");
            }

            return null;
        }

        try
        {
            using var process = Process.GetProcessById(descriptor.Pid);
            if (process.HasExited) { return null; }
        }
        catch (ArgumentException) { return null; }

        if (!PathHelper.ArePathsEqual(descriptor.AppDataPath, PathUtil.AppDataPath))
        {
            throw new CliExitException(1, $"The running Host does not identify the selected environment '{PathUtil.AppDataPath}'. Stop it and select the intended environment before erasing.");
        }

        return descriptor;
    }

    private static void ValidateTargets(IReadOnlyList<EraseTarget> targets)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        using var configuration = new ConfigurationManager();
        configuration.SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true).AddEnvironmentVariables();
        var connection = ApplicationDatabaseConnection.ResolveConnectionString(configuration.GetConnectionString("DefaultConnection"));
        var database = new SqliteConnectionStringBuilder(connection).DataSource;
        if (!Path.IsPathFullyQualified(database) || !PathHelper.IsPathWithinDirectory(database, PathUtil.AppDataPath))
        {
            throw new CliExitException(1, "Complete erase cannot use an external or non-file database override. Select Oscova's local environment database first.");
        }

        foreach (var target in targets)
        {
            ValidatePath(target.Path);
            if (target.RequiresDescriptor && Directory.Exists(target.Path)) { _ = ReadDescriptor(target.Path); }
        }
    }

    private static void ValidatePath(string path, bool inspectChildren = true, bool rejectProtectedRoots = true)
    {
        var full = Path.GetFullPath(path);
        var anchor = Path.GetPathRoot(full)!;
        if ((File.GetAttributes(anchor) & FileAttributes.ReparsePoint) != 0 || PathHelper.HasSymlinkInPath(full, anchor))
        {
            throw new CliExitException(1, $"Erase refuses a linked or inaccessible path: '{full}'.");
        }

        if (rejectProtectedRoots)
        {
            var protectedPaths = new[]
            {
                anchor, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.GetDirectoryName(RuntimeDescriptorWriter.RuntimePath) is { } cliRoot ? Directory.GetParent(cliRoot)!.FullName : anchor,
                AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!
            };
            if (protectedPaths.Any(value => PathHelper.IsPathWithinDirectory(value, full)))
            {
                throw new CliExitException(1, $"Erase refuses a protected directory or its ancestor: '{full}'.");
            }
        }

        if (!inspectChildren || !Directory.Exists(full)) { return; }

        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new CliExitException(1, $"Erase refuses a linked entry: '{entry}'.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                ValidatePath(entry, rejectProtectedRoots: false);
            }
        }
    }

    private static byte[] ReadDescriptor(string directory)
    {
        var path = Path.Combine(directory, EnvironmentDataFormat.FileName);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > 65536) { throw new CliExitException(1, $"No valid Oscova ownership descriptor in '{directory}'."); }

        var bytes = File.ReadAllBytes(path);
        var descriptor = JsonSerializer.Deserialize<EnvironmentDataFormat>(bytes, CliJson.Options);
        if (descriptor is null || descriptor.DataFormatVersion <= 0 || !NuGetVersion.TryParse(descriptor.AppVersion, out _))
        {
            throw new CliExitException(1, $"No valid Oscova ownership descriptor in '{directory}'.");
        }

        return bytes;
    }

    private static void DeleteExternalEnvironment(string directory)
    {
        var descriptor = ReadDescriptor(directory);
        var descriptorPath = Path.Combine(directory, EnvironmentDataFormat.FileName);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (PathHelper.ArePathsEqual(entry, descriptorPath)) { continue; }

            if (Directory.Exists(entry)) { Directory.Delete(entry, recursive: true); }
            else { File.Delete(entry); }
        }

        File.Delete(descriptorPath);
        try { Directory.Delete(directory, recursive: false); }
        catch (Exception removalError) when (removalError is IOException or UnauthorizedAccessException)
        {
            try { File.WriteAllBytes(descriptorPath, descriptor); }
            catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not remove '{directory}' or restore its ownership descriptor: {restoreError.Message}", removalError);
            }

            throw;
        }
    }
}

/// <summary>Identifies an owned erase target and whether retry requires preserving its descriptor.</summary>
public sealed record EraseTarget(string Path, bool RequiresDescriptor);
