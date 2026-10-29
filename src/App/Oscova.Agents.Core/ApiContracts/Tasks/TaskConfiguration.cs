using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Definitions;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Core.ApiContracts.Tasks;

/// <summary>Scheduled task list with latest runs and selectable agents/providers.</summary>
public sealed class TaskConfiguration
{
    public IList<AgentTaskDefinition> Tasks { get; set; } = [];
    /// <summary>Selected display text, with null when a managed default is unavailable.</summary>
    public IReadOnlyDictionary<string, string?> Instructions { get; set; } = new Dictionary<string, string?>();
    public IDictionary<string, AgentTaskRun> LatestRuns { get; set; } = new Dictionary<string, AgentTaskRun>();
    public IList<string> RunningTaskIds { get; set; } = [];
    public IList<SelectableAgent> AvailableAgents { get; set; } = [];
    public IList<AgentDefinition> AvailableAgentDefinitions { get; set; } = [];
    public IList<AgentReference> ExposableAgents { get; set; } = [];
    public IList<ToolGroup> ToolGroups { get; set; } = [];
    public IList<SkillDefinition> AvailableSkills { get; set; } = [];
    public IList<string> AvailableWorkspaces { get; set; } = [];
    public IList<SelectableModelProvider> AvailableModelProviders { get; set; } = [];
    public string UserTimeZoneId { get; set; } = string.Empty;
}
