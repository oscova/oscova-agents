namespace Oscova.Agents.Plugins;

/// <summary>Describes a tracked download into the Host's model storage.</summary>
public sealed record PluginDownloadRequest(string Id, string Url, string DestinationPath);
