using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Definitions;

namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>Agent configuration response including all agents, tools, skills, and users.</summary>
public sealed class AgentConfiguration
{
    public AgentDefinition MainAgent { get; set; } = new();
    public IReadOnlyList<AgentDefinition> CustomAgents { get; set; } = [];
    public IReadOnlyList<AgentReference> ExposableAgents { get; set; } = [];
    public IReadOnlyList<ToolGroup> ToolGroups { get; set; } = [];
    public IReadOnlyList<SkillDefinition> AvailableSkills { get; set; } = [];
    public IReadOnlyList<SelectableModelProvider> AvailableModelProviders { get; set; } = [];
    public IReadOnlyList<string> AvailableWorkspaces { get; set; } = [];
    public IReadOnlyList<SelectableUser> AvailableUsers { get; set; } = [];
    public bool CanManageSharedAgents { get; set; }
    public bool CanUseSharedFolder { get; set; }
    public IReadOnlyDictionary<string, int> GoalIterationLimits { get; set; } = new Dictionary<string, int>();
}

/// <summary>Captures a personal goal limit without modifying a shared agent definition.</summary>
public sealed record SaveAgentGoalLimitRequest(int? MaxIterations);
