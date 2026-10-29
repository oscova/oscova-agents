
namespace Oscova.Agents.Core.ApiContracts.Nodes;

/// <summary>Supplies the project and run identity admitted by the central server.</summary>
public sealed record ProjectNodeContext(
    string ProjectId,
    string RootPath,
    string? AgentId,
    string? ProviderId,
    bool AirGapped,
    bool AllowSandboxNetworkAccess = false,
    bool FullAccess = false,
    string? ConfiguredProviderId = null);
