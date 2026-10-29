using System.Reflection;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Plugins.Registry;

/// <summary>Admits plugin declarations against the framework supplied by the host.</summary>
public static class PluginFrameworkCompatibility
{
    private static readonly NuGetFramework TargetFramework = NuGetFramework.ParseFolder("net10.0");

    /// <summary>Gets the bundled framework package version independently of the App version.</summary>
    public static NuGetVersion CurrentVersion { get; } = NuGetVersion.Parse(
        typeof(IAgentPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

    /// <summary>Returns the correction required for an absent, malformed or nonmatching range.</summary>
    public static string? GetIncompatibility(string? declaration, NuGetVersion frameworkVersion)
    {
        if (string.IsNullOrWhiteSpace(declaration) || !VersionRange.TryParse(declaration, out var range))
        {
            return "The plugin must declare a valid frameworkVersionRange in oscova-plugin.json.";
        }

        return range.Satisfies(frameworkVersion) ? null :
            $"The plugin supports Oscova.Agents {declaration}, but this application uses {frameworkVersion.ToNormalizedString()}. Update it from Plugins when a compatible release is available.";
    }

    /// <summary>Reads the framework dependency from the applicable NuGet target group.</summary>
    public static string? GetDependencyRange(IEnumerable<PackageDependencyGroup> groups)
    {
        var candidates = groups.ToArray();
        var nearest = new FrameworkReducer().GetNearest(TargetFramework, candidates.Select(group => group.TargetFramework));
        if (nearest is null) { return null; }

        var declarations = candidates.Where(group => group.TargetFramework.Equals(nearest))
            .SelectMany(group => group.Packages)
            .Where(package => string.Equals(package.Id, "Oscova.Agents", StringComparison.OrdinalIgnoreCase))
            .Select(package => package.VersionRange?.ToNormalizedString())
            .Distinct(StringComparer.Ordinal).ToArray();
        return declarations.Length == 1 ? declarations[0] : null;
    }

    /// <summary>Disables incompatible installations without removing their files or selection records.</summary>
    public static async Task AdmitAsync(string pluginsRoot, PluginActivationSettings settings, CancellationToken cancellationToken)
    {
        var enabled = settings.EnabledPluginIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var inspection in AgentPluginLoader.InspectDirectory(pluginsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inspection.Manifest is not { } manifest) { continue; }

            var reason = GetIncompatibility(manifest.FrameworkVersionRange, CurrentVersion);
            if (reason is null) { continue; }

            enabled.Remove(manifest.Id);
            reasons[manifest.Id] = reason;
        }

        var reasonsChanged = reasons.Count != settings.CompatibilityDisabledReasons.Count ||
            reasons.Any(item => !settings.CompatibilityDisabledReasons.TryGetValue(item.Key, out var saved) || saved != item.Value);
        if (enabled.SetEquals(settings.EnabledPluginIds) && !reasonsChanged) { return; }

        settings.SetEnabledPluginIds(enabled);
        settings.SetCompatibilityDisabledReasons(reasons);
        await settings.SaveAsync().ConfigureAwait(false);
    }
}
