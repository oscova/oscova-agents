namespace Oscova.Agents.Core.Nodes;

/// <summary>Identifies an enrolled execution device independently of its display name.</summary>
public sealed record NodeDefinition(string Id, string Name);

/// <summary>Binds an authenticated device connection to its central account.</summary>
public sealed record NodeIdentity(string UserId, string NodeId);
