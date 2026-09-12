namespace Oscova.Agents.Plugins;

/// <summary>
/// Describes one plugin manifest found on disk without executing its assembly.
/// </summary>
/// <param name="ManifestPath">Absolute path to the discovered manifest file, or the plugin scan root when directory enumeration fails.</param>
/// <param name="Manifest">Validated manifest data, when inspection succeeded.</param>
/// <param name="Diagnostic">Inspection error that prevents this installation from loading.</param>
public sealed record AgentPluginInspection(
    string ManifestPath,
    AgentPluginManifest? Manifest,
    AgentPluginLoadDiagnostic? Diagnostic);
