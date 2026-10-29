using Oscova.Agents.Core.Settings;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.ApiContracts.Tools;

/// <summary>
/// Describes the current per-user governance state for available tools.
/// </summary>
public sealed class ToolGovernanceSummary
{
    public IReadOnlyList<ToolGovernanceEntrySummary> Tools { get; set; } = [];
}

/// <summary>
/// Describes user-authored policy and current quota usage for one tool.
/// </summary>
public sealed class ToolGovernanceEntrySummary
{
    public string QualifiedName { get; set; } = string.Empty;
    public NetworkScope DeclaredNetworkScope { get; set; }
    public NetworkScope EffectiveNetworkScope { get; set; }
    public bool HasNetworkScopeOverride { get; set; }
    public ToolCallLimit? CallLimit { get; set; }
    public ToolQuotaUsageSummary? Usage { get; set; }
}
