using Oscova.Agents.Collections;
using System.Reflection;

namespace Oscova.Agents.Host.Services;

/// <summary>
/// Reports product and framework assembly versions for user-facing diagnostics.
/// </summary>
public sealed class RuntimeVersionService
{
    /// <summary>
    /// Gets the current host and framework versions from generated assembly metadata.
    /// </summary>
    public RuntimeVersionSnapshot GetSnapshot()
    {
        return new RuntimeVersionSnapshot(
            Host: GetComponentVersion(typeof(WebHostStartup).Assembly),
            Framework: GetComponentVersion(typeof(AgentToolCollection).Assembly));
    }

    private static RuntimeComponentVersion GetComponentVersion(Assembly assembly)
    {
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var assemblyVersion = assembly.GetName().Version?.ToString();
        var fullVersion = FirstNonEmpty(informationalVersion, fileVersion, assemblyVersion, "dev");

        return new RuntimeComponentVersion(
            DisplayVersion: TrimMetadata(fullVersion),
            FullVersion: fullVersion);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "dev";
    }

    private static string TrimMetadata(string version)
    {
        var metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex > 0
            ? version[..metadataIndex]
            : version;
    }
}

/// <summary>
/// Current runtime versions displayed in host settings.
/// </summary>
public sealed record RuntimeVersionSnapshot(
    RuntimeComponentVersion Host,
    RuntimeComponentVersion Framework);

/// <summary>
/// Version strings for a runtime component, including a compact display value and the full assembly value.
/// </summary>
public sealed record RuntimeComponentVersion(
    string DisplayVersion,
    string FullVersion);
