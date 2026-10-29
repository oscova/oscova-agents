using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Tasks;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Skills;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Infrastructure.Tasks;

/// <summary>
/// Builds the live user catalog used to validate task-local run scopes before saving or executing them.
/// </summary>
public static class TaskRunScopeValidationCatalogFactory
{
    /// <summary>
    /// Creates a validation catalog from the current user runtime surface.
    /// </summary>
    public static TaskRunScopeValidationCatalog Create(
        UserServiceScope userScope,
        IUserDirectoryService userDirectoryService)
        => Create(userScope.AppService, userScope.CrewService, userScope.UserId, userDirectoryService);

    /// <summary>
    /// Creates a validation catalog from the individual user runtime services.
    /// </summary>
    public static TaskRunScopeValidationCatalog Create(
        AppService appService,
        AgentCompositionService crewService,
        string userId,
        IUserDirectoryService userDirectoryService)
    {
        var availableAgents = new[] { appService.MainAgentDefinition }
            .Concat(appService.CustomAgentDefinitions);
        var skillNames = new SkillDiscoverer()
            .Discover(userDirectoryService.GetSkillsDirectoryPath(userId))
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new TaskRunScopeValidationCatalog
        {
            // Host commands require a foreground decision and are never selectable by unattended tasks.
            ToolNames = crewService.Tools
                .Select(tool => tool.Name)
                .Concat(crewService.MemoryToolNames)
                .Where(name => !string.Equals(
                    name,
                    HostShellToolset.QualifiedToolName,
                    StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            ExposedAgentIds = availableAgents
                .Where(agent => agent.IsExposedAsTool)
                .Select(agent => agent.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            SkillNames = skillNames,
            WorkspaceNames = userDirectoryService
                .ListWorkspaces(userId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }
}
