namespace Oscova.Agents.Core.ApiContracts.Nodes;

/// <summary>Summarizes plugin readiness without exposing another account's device identities.</summary>
public sealed record NodePluginStatus(string PluginId, IReadOnlyList<string> Ready,
    IReadOnlyList<string> Preparing, IReadOnlyList<string> Unavailable);
