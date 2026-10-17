using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Oscova.Agents.Plugins;

/// <summary>
/// Resolves plugin-local dependencies while sharing the host SDK assembly identity.
/// </summary>
internal sealed class AgentPluginLoadContext(string pluginAssemblyPath) : AssemblyLoadContext(isCollectible: false)
{
    private readonly string _pluginAssemblyPath = pluginAssemblyPath;
    private readonly string _nativeDirectory = Path.Combine(
        Path.GetDirectoryName(pluginAssemblyPath)!,
        "runtimes",
        AgentPluginLoader.SupportedRuntimeIdentifier,
        "native");
    private readonly AssemblyDependencyResolver _resolver = new(pluginAssemblyPath);

    /// <summary>
    /// Loads the primary plugin assembly without holding a file lock on the DLL.
    /// </summary>
    public Assembly LoadPluginAssembly()
    {
        using var stream = File.OpenRead(_pluginAssemblyPath);
        return LoadFromStream(stream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && (AgentPluginLoader.SharedAssemblyNames.Contains(assemblyName.Name) || assemblyName.Name.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal)))
        {
            return null;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is null)
        {
            return null;
        }

        // Stream loading avoids retaining a Windows file lock on managed dependencies.
        using var stream = File.OpenRead(assemblyPath);
        return LoadFromStream(stream);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath is not null)
        {
            return LoadNativeLibrary(libraryPath);
        }

        // Some SDK imports do not match their packaged filename (for example, libfoo vs foo.dll).
        // Keep the fallback inside this plugin's standard current-RID native directory.
        var fileName = GetPlatformLibraryName(Path.GetFileName(unmanagedDllName));
        var pluginPath = Path.Combine(_nativeDirectory, fileName);
        return File.Exists(pluginPath) ? LoadNativeLibrary(pluginPath) : IntPtr.Zero;
    }

    /// <summary>
    /// Loads through Windows extended-path syntax because the global-tool store and plugin package
    /// segments can exceed the native loader's legacy path limit.
    /// </summary>
    private IntPtr LoadNativeLibrary(string libraryPath)
    {
        return LoadUnmanagedDllFromPath(GetNativeLibraryPath(libraryPath));
    }

    /// <summary>Converts both drive and UNC paths without changing non-Windows plugin paths.</summary>
    internal static string GetNativeLibraryPath(string libraryPath)
    {
        if (!OperatingSystem.IsWindows() || libraryPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return libraryPath;
        }

        return libraryPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + libraryPath[2..]
            : @"\\?\" + libraryPath;
    }

    private static string GetPlatformLibraryName(string name)
    {
        if (Path.HasExtension(name))
        {
            return name;
        }

        if (OperatingSystem.IsWindows())
        {
            var baseName = name.StartsWith("lib", StringComparison.Ordinal) ? name[3..] : name;
            return $"{baseName}.dll";
        }

        var prefix = name.StartsWith("lib", StringComparison.Ordinal) ? string.Empty : "lib";
        return OperatingSystem.IsMacOS() ? $"{prefix}{name}.dylib" : $"{prefix}{name}.so";
    }
}
