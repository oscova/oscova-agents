namespace Oscova.Agents.Core.ApiContracts.Nodes;

/// <summary>Binds a transport to its project, or a short setup connection when no project is selected.</summary>
public sealed record McpNodeContext(string SessionId, string? ProjectId, string? RootPath, bool AirGapped);
