using System.Collections.Generic;
using System;
using System.Linq;

namespace Oscova.Agents.Plugins;

/// <summary>
/// Describes a plugin assembly discovered on disk.
/// </summary>
public sealed class AgentPluginManifest
{
    /// <summary>
    /// Gets the stable plugin identifier used for duplicate detection.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the display name shown for the plugin.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the plugin version string from the manifest.
    /// </summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gets the declared supported framework package range for admission by the harness.</summary>
    public string FrameworkVersionRange { get; init; } = string.Empty;

    /// <summary>Gets the supported release runtime identifiers. An absent list permits every release platform.</summary>
    public IReadOnlyList<string>? SupportedRuntimeIdentifiers { get; init; }

    /// <summary>Gets advisory hardware requirements by release runtime identifier.</summary>
    public IReadOnlyDictionary<string, PluginHardwareRequirement>? HardwareRequirements { get; init; }

    /// <summary>Checks whether the plugin declares support for the supplied release runtime identifier.</summary>
    public bool SupportsRuntimeIdentifier(string runtimeIdentifier) =>
        SupportedRuntimeIdentifiers?.Contains(runtimeIdentifier, StringComparer.Ordinal) ?? true;

    /// <summary>
    /// Gets the plugin assembly path relative to the manifest directory.
    /// </summary>
    public string Assembly { get; init; } = string.Empty;

    /// <summary>
    /// Gets the fully qualified entry type that implements <see cref="IAgentPlugin"/>.
    /// </summary>
    public string EntryType { get; init; } = string.Empty;

    /// <summary>Gets extra files to retain from restored dependency packages without running their build targets.</summary>
    public IReadOnlyList<AgentPluginDependencyAsset> DependencyAssets { get; init; } = [];
}
