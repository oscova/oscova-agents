using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Plugins;

/// <summary>
/// Loads trusted in-process plugins from plugin directories.
/// </summary>
public sealed class AgentPluginLoader
{
    /// <summary>
    /// Manifest file name searched under each plugin directory.
    /// </summary>
    public const string ManifestFileName = "oscova-plugin.json";

    /// <summary>Gets the portable release RID, since distro .NET builds report names such as ubuntu.26.04-x64.</summary>
    public static string SupportedRuntimeIdentifier =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" :
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "linux-x64" :
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" :
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "osx-x64" :
        throw new PlatformNotSupportedException("Oscova supports win-x64, linux-x64, osx-x64, and osx-arm64.");

    internal static IReadOnlySet<string> SharedAssemblyNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        typeof(IAgentPlugin).Assembly.GetName().Name!,
        typeof(IChatClient).Assembly.GetName().Name!,
        typeof(ILoggerFactory).Assembly.GetName().Name!,
        // Runtime HTTP JSON APIs require the same serializer type identity as plugin dependencies.
        typeof(JsonSerializer).Assembly.GetName().Name!,
        typeof(Microsoft.Agents.AI.Tools.Shell.ShellExecutor).Assembly.GetName().Name!,
        "Microsoft.AspNetCore.Components",
        "Microsoft.AspNetCore.Components.Web",
        "Microsoft.Extensions.Localization.Abstractions"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Loads every valid plugin manifest under <paramref name="pluginRoot"/>.
    /// </summary>
    public static AgentPluginCatalog LoadDirectory(string pluginRoot)
    {
        return LoadInspectedPlugins(InspectDirectory(pluginRoot), enabledPluginIds: null);
    }

    /// <summary>
    /// Loads only valid plugins whose stable identifiers are present in <paramref name="enabledPluginIds"/>.
    /// </summary>
    public static AgentPluginCatalog LoadDirectory(
        string pluginRoot,
        IReadOnlySet<string> enabledPluginIds)
    {
        ArgumentNullException.ThrowIfNull(enabledPluginIds);
        return LoadInspectedPlugins(InspectDirectory(pluginRoot), enabledPluginIds);
    }

    /// <summary>
    /// Reads plugin manifests and validates their referenced files without loading plugin assemblies.
    /// </summary>
    public static IReadOnlyList<AgentPluginInspection> InspectDirectory(string pluginRoot)
    {
        if (string.IsNullOrWhiteSpace(pluginRoot) || !Directory.Exists(pluginRoot))
        {
            return [];
        }

        var inspections = new List<AgentPluginInspection>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] manifestPaths;
        try
        {
            manifestPaths = [.. Directory
                .EnumerateFiles(pluginRoot, ManifestFileName, SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(pluginRoot, path).StartsWith(".staging-", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            return
            [
                new AgentPluginInspection(
                    pluginRoot,
                    null,
                    new AgentPluginLoadDiagnostic(
                    AgentPluginLoadDiagnosticLevel.Error,
                    pluginRoot,
                    $"Plugin directory could not be searched: {ex.Message}"))
            ];
        }

        foreach (var manifestPath in manifestPaths)
        {
            var inspection = InspectPlugin(manifestPath);
            if (inspection.Diagnostic is not null || inspection.Manifest is null)
            {
                inspections.Add(inspection);
                continue;
            }

            if (!seenIds.Add(inspection.Manifest.Id))
            {
                inspections.Add(inspection with
                {
                    Diagnostic = new AgentPluginLoadDiagnostic(
                        AgentPluginLoadDiagnosticLevel.Error,
                        manifestPath,
                        $"Duplicate plugin id '{inspection.Manifest.Id}'.")
                });
                continue;
            }

            inspections.Add(inspection);
        }

        return inspections;
    }

    private static AgentPluginCatalog LoadInspectedPlugins(
        IReadOnlyList<AgentPluginInspection> inspections,
        IReadOnlySet<string>? enabledPluginIds)
    {
        var plugins = new List<LoadedAgentPlugin>();
        var diagnostics = inspections
            .Where(inspection => inspection.Diagnostic is not null)
            .Select(inspection => inspection.Diagnostic!)
            .ToList();

        foreach (var inspection in inspections)
        {
            if (inspection.Manifest is null || inspection.Diagnostic is not null)
            {
                continue;
            }

            if (enabledPluginIds?.Contains(inspection.Manifest.Id) == false)
            {
                continue;
            }

            var plugin = LoadPlugin(inspection.ManifestPath, inspection.Manifest, diagnostics);
            if (plugin is not null)
            {
                plugins.Add(plugin);
            }
        }

        return new AgentPluginCatalog(plugins, diagnostics);
    }

    private static AgentPluginInspection InspectPlugin(string manifestPath)
    {
        try
        {
            var manifest = ReadManifest(manifestPath);
            var validationMessage = ValidateManifest(manifest);
            if (validationMessage is not null)
            {
                return InvalidInspection(manifestPath, manifest, validationMessage);
            }

            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            if (!TryResolveAssemblyPath(manifestDirectory, manifest!.Assembly, out var assemblyPath, out var assemblyPathError))
            {
                return InvalidInspection(manifestPath, manifest, assemblyPathError);
            }

            if (!File.Exists(assemblyPath))
            {
                return InvalidInspection(
                    manifestPath,
                    manifest,
                    $"Plugin assembly '{manifest.Assembly}' was not found.");
            }

            return new AgentPluginInspection(manifestPath, manifest, null);
        }
        catch (Exception ex)
        {
            return InvalidInspection(manifestPath, null, $"Plugin manifest could not be inspected: {ex.Message}");
        }
    }

    private static AgentPluginInspection InvalidInspection(
        string manifestPath,
        AgentPluginManifest? manifest,
        string message)
    {
        return new AgentPluginInspection(
            manifestPath,
            manifest,
            new AgentPluginLoadDiagnostic(
                AgentPluginLoadDiagnosticLevel.Error,
                manifestPath,
                message));
    }

    private static LoadedAgentPlugin? LoadPlugin(
        string manifestPath,
        AgentPluginManifest manifest,
        List<AgentPluginLoadDiagnostic> diagnostics)
    {
        try
        {
            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            if (!TryResolveAssemblyPath(manifestDirectory, manifest.Assembly, out var assemblyPath, out var assemblyPathError))
            {
                diagnostics.Add(new AgentPluginLoadDiagnostic(
                    AgentPluginLoadDiagnosticLevel.Error,
                    manifestPath,
                    assemblyPathError));
                return null;
            }

            if (!File.Exists(assemblyPath))
            {
                diagnostics.Add(new AgentPluginLoadDiagnostic(
                    AgentPluginLoadDiagnosticLevel.Error,
                    manifestPath,
                    $"Plugin assembly '{manifest.Assembly}' was not found."));
                return null;
            }

            var loadContext = new AgentPluginLoadContext(assemblyPath);
            var assembly = loadContext.LoadPluginAssembly();
            // The harness admits inspected manifests using their declared framework range before loading.
            var entryType = assembly.GetType(manifest.EntryType, throwOnError: false);
            if (entryType is null)
            {
                diagnostics.Add(new AgentPluginLoadDiagnostic(
                    AgentPluginLoadDiagnosticLevel.Error,
                    manifestPath,
                    $"Plugin entry type '{manifest.EntryType}' was not found."));
                return null;
            }

            if (!typeof(IAgentPlugin).IsAssignableFrom(entryType))
            {
                diagnostics.Add(new AgentPluginLoadDiagnostic(
                    AgentPluginLoadDiagnosticLevel.Error,
                    manifestPath,
                    $"Plugin entry type '{manifest.EntryType}' must implement {nameof(IAgentPlugin)}."));
                return null;
            }

            if (Activator.CreateInstance(entryType) is not IAgentPlugin entry)
            {
                diagnostics.Add(new AgentPluginLoadDiagnostic(
                    AgentPluginLoadDiagnosticLevel.Error,
                    manifestPath,
                    $"Plugin entry type '{manifest.EntryType}' must have a public parameterless constructor."));
                return null;
            }

            var builder = new AgentPluginBuilder();
            entry.Configure(builder);
            foreach (var message in builder.Diagnostics)
            {
                diagnostics.Add(new AgentPluginLoadDiagnostic(AgentPluginLoadDiagnosticLevel.Warning,
                    manifestPath, message));
            }
            if (builder.ResourceType is { } resourceType && resourceType.Assembly != assembly)
            {
                throw new InvalidOperationException("Plugin display resources must be declared by the plugin assembly.");
            }

            return new LoadedAgentPlugin(manifest, assembly, manifestDirectory, builder);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new AgentPluginLoadDiagnostic(
                AgentPluginLoadDiagnosticLevel.Error,
                manifestPath,
                $"Plugin failed to load: {ex.Message}"));
            return null;
        }
    }

    private static bool TryResolveAssemblyPath(
        string manifestDirectory,
        string assemblyPathValue,
        out string assemblyPath,
        out string errorMessage)
    {
        assemblyPath = string.Empty;
        errorMessage = string.Empty;

        if (Path.IsPathRooted(assemblyPathValue))
        {
            errorMessage = "Plugin assembly path must be relative to the plugin manifest directory.";
            return false;
        }

        assemblyPath = Path.GetFullPath(Path.Combine(manifestDirectory, assemblyPathValue));
        var relativePath = Path.GetRelativePath(manifestDirectory, assemblyPath);
        if (relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            errorMessage = "Plugin assembly path must stay within the plugin manifest directory.";
            return false;
        }

        return true;
    }

    private static AgentPluginManifest? ReadManifest(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        return JsonSerializer.Deserialize<AgentPluginManifest>(stream, JsonOptions);
    }

    private static string? ValidateManifest(AgentPluginManifest? manifest)
    {
        if (manifest is null)
        {
            return "Plugin manifest could not be read.";
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            return "Plugin manifest must set Id.";
        }

        if (HasSurroundingWhitespace(manifest.Id))
        {
            return "Plugin manifest Id must not contain leading or trailing whitespace.";
        }

        if (manifest.Id.Contains(':'))
        {
            return "Plugin manifest Id must not contain ':' because it separates credential namespaces.";
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            return "Plugin manifest must set Name.";
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return "Plugin manifest must set Version.";
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            return "Plugin manifest must set Assembly.";
        }

        if (HasSurroundingWhitespace(manifest.Assembly))
        {
            return "Plugin manifest Assembly must not contain leading or trailing whitespace.";
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            return "Plugin manifest must set EntryType.";
        }

        if (HasSurroundingWhitespace(manifest.EntryType))
        {
            return "Plugin manifest EntryType must not contain leading or trailing whitespace.";
        }

        return null;
    }

    private static bool HasSurroundingWhitespace(string value)
    {
        return !string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }
}
