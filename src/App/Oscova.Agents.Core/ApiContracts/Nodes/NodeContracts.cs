using System.Text.Json;
using Oscova.Agents.Core.Nodes;

namespace Oscova.Agents.Core.ApiContracts.Nodes;

/// <summary>Requests a named device credential for the signed-in account.</summary>
public sealed record EnrollNodeRequest(string Name);

/// <summary>Describes an owned device without exposing its authentication secret.</summary>
public sealed record NodeSummary(NodeDefinition Node, bool Connected, IReadOnlyDictionary<string, int> Capabilities);
/// <summary>Returns the node credential once to the enrolling CLI.</summary>
public sealed record NodeEnrollment(NodeDefinition Node, string Token);

/// <summary>Advertises operations implemented by one versioned device capability.</summary>
public sealed record NodeCapability(string Id, int Version, IReadOnlyList<NodeTool> Tools);

/// <summary>Carries a registered function's metadata without implying permission to invoke it.</summary>
public sealed record NodeTool(string Name, string Description, JsonElement Schema);

/// <summary>Describes the connected runtime and its supported capability contracts.</summary>
public sealed record NodeHello(int ProtocolVersion, string HostVersion, string FrameworkVersion,
    IReadOnlyList<NodeCapability> Capabilities);

/// <summary>Returns current account permissions separately from supported device operations.</summary>
public sealed record NodePolicy(IReadOnlyList<string> GrantedPermissions);

/// <summary>Identifies one server-issued invocation and its capability-specific execution context.</summary>
public sealed record NodeInvocation(string Id, string ExecutionId, string Capability, string Tool,
    JsonElement Context, JsonElement Arguments);

/// <summary>Returns a completed invocation or an explicit failure without implying rollback.</summary>
public sealed record NodeReply(JsonElement? Result, string? Failure, IReadOnlyList<NodeOutput> Outputs,
    string? FilePath = null);

/// <summary>Returns bounded tool output for storage under the server user's existing protection.</summary>
public sealed record NodeOutput(string Marker, string Label, string Content);

/// <summary>Describes the account's devices and current grants for project selection.</summary>
public sealed record NodeCatalog(IReadOnlyList<NodeSummary> Nodes, IReadOnlyList<string> Permissions, bool IsOperator)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ProjectsEnabled => Permissions.Contains(Core.Auth.UserPermissionKeys.Projects, StringComparer.Ordinal);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanCreateProjects => ProjectsEnabled && (IsOperator || Nodes.Any(IsProjectNode));

    /// <summary>Reports whether the named project device is currently usable in the browser.</summary>
    public bool IsProjectAvailable(Oscova.Agents.Core.Settings.ProjectDefinition project) =>
        ProjectsEnabled && (project.NodeId is null ? IsOperator :
            Nodes.Any(node => node.Node.Id == project.NodeId && IsProjectNode(node)));

    /// <summary>Checks the project capability version understood by this server.</summary>
    public static bool IsProjectNode(NodeSummary node) => node.Connected &&
        node.Capabilities.TryGetValue(ProjectCapability.Id, out var version) &&
        version == ProjectCapability.Version;
}
