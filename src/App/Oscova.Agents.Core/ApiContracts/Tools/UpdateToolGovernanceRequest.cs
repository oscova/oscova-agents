using Oscova.Agents.Core.Settings;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.ApiContracts.Tools;

/// <summary>
/// Updates user-authored governance policy for one qualified tool.
/// </summary>
public sealed class UpdateToolGovernanceRequest
{
    public string QualifiedName { get; set; } = string.Empty;
    public NetworkScope? NetworkScopeOverride { get; set; }
    public ToolCallLimit? CallLimit { get; set; }
}
