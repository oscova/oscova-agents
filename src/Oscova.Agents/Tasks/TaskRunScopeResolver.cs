using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oscova.Agents.Definitions;
using Oscova.Agents.Policies;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Resolves task-local runtime settings into an effective one-run agent surface.
/// </summary>
public sealed class TaskRunScopeResolver
{
    private readonly StringComparer _nameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Resolves an optional task run scope or throws with an actionable validation message.
    /// </summary>
    public TaskRunSurface ResolveOrThrow(
        TaskRunScope? scope,
        AgentDefinition selectedAgent,
        Func<AgentDefinition, IReadOnlyList<AllowedDirectory>> resolveDirectories,
        TaskRunScopeValidationCatalog? catalog = null)
    {
        if (!TryResolve(scope, selectedAgent, resolveDirectories, out var surface, out var error, catalog))
        {
            throw new InvalidOperationException(error ?? "Task run scope is invalid.");
        }

        return surface!;
    }

    /// <summary>
    /// Tries to resolve an optional task run scope into an effective runtime surface.
    /// </summary>
    public bool TryResolve(
        TaskRunScope? scope,
        AgentDefinition selectedAgent,
        Func<AgentDefinition, IReadOnlyList<AllowedDirectory>> resolveDirectories,
        out TaskRunSurface? surface,
        out string? error,
        TaskRunScopeValidationCatalog? catalog = null)
    {
        try
        {
            var normalizedScope = scope is null ? null : NormalizeScopeOrThrow(scope, catalog, _nameComparer);
            var effectiveScope = normalizedScope ?? TaskRunScope.FromAgent(selectedAgent);
            if (normalizedScope is null && catalog?.ToolNames is { } availableToolNames)
            {
                // Agent-settings mode stays implicit but still respects the task runtime's tool catalogue.
                ToolReferenceSanitizer.RemoveUnavailableTools(
                    effectiveScope.AllowedToolNames,
                    effectiveScope.PinnedToolNames,
                    effectiveScope.ToolPolicies,
                    availableToolNames);
            }

            var effectiveAgent = BuildEffectiveAgent(selectedAgent, effectiveScope);
            var directories = resolveDirectories(effectiveAgent);

            surface = new TaskRunSurface
            {
                EffectiveAgent = effectiveAgent,
                Directories = directories,
                NormalizedRunScope = normalizedScope
            };
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            surface = null;
            error = exception.Message;
            return false;
        }
    }

    private static TaskRunScope NormalizeScopeOrThrow(
        TaskRunScope scope,
        TaskRunScopeValidationCatalog? catalog,
        StringComparer nameComparer)
    {
        var allowedToolNames = NormalizeNames(scope.AllowedToolNames, nameComparer);
        ValidateNoManagedTools(allowedToolNames);
        ValidateKnownValues("Tool", allowedToolNames, catalog?.ToolNames);

        var pinnedToolNames = NormalizeNames(scope.PinnedToolNames, nameComparer);
        ValidateKnownValues("Pinned tool", pinnedToolNames, allowedToolNames.ToHashSet(nameComparer));

        var agentToolIds = NormalizeNames(scope.AgentToolIds, nameComparer);
        ValidateKnownValues("Agent tool", agentToolIds, catalog?.ExposedAgentIds);

        var allowedSkillNames = NormalizeNames(scope.AllowedSkillNames, nameComparer);
        ValidateKnownValues("Skill", allowedSkillNames, catalog?.SkillNames);

        var allowedWorkspaces = NormalizeNames(scope.AllowedWorkspaces, nameComparer);
        ValidateKnownValues("Workspace", allowedWorkspaces, catalog?.WorkspaceNames);

        var toolPolicies = NormalizeToolPolicies(scope.ToolPolicies, allowedToolNames, nameComparer);
        var allowedDirectories = NormalizeDirectories(scope.AllowedDirectories, nameComparer);

        return new TaskRunScope
        {
            AllowedToolNames = allowedToolNames,
            ToolPolicies = toolPolicies,
            IsToolSearchEnabled = scope.IsToolSearchEnabled,
            PinnedToolNames = pinnedToolNames,
            IsCompactionEnabled = scope.IsCompactionEnabled,
            IsMemoryEnabled = scope.IsMemoryEnabled,
            IsTodoEnabled = scope.IsTodoEnabled,
            IsLocalOnly = scope.IsLocalOnly,
            AgentToolIds = agentToolIds,
            AllowedSkillNames = allowedSkillNames,
            AllowedWorkspaces = allowedWorkspaces,
            AllowedDirectories = allowedDirectories
        };
    }

    private static AgentDefinition BuildEffectiveAgent(AgentDefinition selectedAgent, TaskRunScope scope)
    {
        return new AgentDefinition
        {
            Id = selectedAgent.Id,
            Name = selectedAgent.Name,
            Description = selectedAgent.Description,
            SystemInstruction = selectedAgent.SystemInstruction,
            InstructionSource = selectedAgent.InstructionSource,
            InstructionMode = selectedAgent.InstructionMode,
            DefaultInstruction = selectedAgent.DefaultInstruction,
            ModelOverrideId = selectedAgent.ModelOverrideId,
            UseCallerModelForConsultations = selectedAgent.UseCallerModelForConsultations,
            AllowedToolNames = [.. scope.AllowedToolNames],
            ToolPolicies = CloneToolPolicies(scope.ToolPolicies),
            IsToolSearchEnabled = scope.IsToolSearchEnabled,
            PinnedToolNames = [.. scope.PinnedToolNames],
            IsCompactionEnabled = scope.IsCompactionEnabled,
            IsMemoryEnabled = scope.IsMemoryEnabled,
            IsTodoEnabled = scope.IsTodoEnabled,
            IsLocalOnly = scope.IsLocalOnly,
            // Task scope can change runtime capabilities, but it must retain the agent's storage classification.
            IsExposedAsTool = selectedAgent.IsExposedAsTool,
            AgentToolIds = [.. scope.AgentToolIds],
            AllowedSkillNames = [.. scope.AllowedSkillNames],
            AllowedWorkspaces = [.. scope.AllowedWorkspaces],
            AllowedDirectories = [.. scope.AllowedDirectories],
            IsShared = selectedAgent.IsShared,
            IsMain = selectedAgent.IsMain,
            OwnerUserId = selectedAgent.OwnerUserId,
            AllowedUserIds = [.. selectedAgent.AllowedUserIds]
        };
    }

    private static List<string> NormalizeNames(IEnumerable<string>? values, StringComparer nameComparer)
    {
        return [.. (values ?? [])
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(nameComparer)];
    }

    private static List<AgentToolPolicy> NormalizeToolPolicies(
        IEnumerable<AgentToolPolicy>? policies,
        IReadOnlyList<string> allowedToolNames,
        StringComparer nameComparer)
    {
        var allowedToolSet = allowedToolNames.ToHashSet(nameComparer);
        var normalized = new Dictionary<string, AgentToolPolicy>(nameComparer);
        foreach (var policy in policies ?? [])
        {
            var toolName = policy.ToolName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(toolName) || policy.ApprovalMode == ToolApprovalMode.Default)
            {
                continue;
            }

            if (!allowedToolSet.Contains(toolName))
            {
                throw new InvalidOperationException($"Tool policy '{toolName}' must reference a selected tool.");
            }

            normalized[toolName] = new AgentToolPolicy
            {
                ToolName = toolName,
                ApprovalMode = policy.ApprovalMode
            };
        }

        return [.. normalized.Values];
    }

    private static List<AgentToolPolicy> CloneToolPolicies(IEnumerable<AgentToolPolicy> policies)
    {
        return [.. policies.Select(policy => new AgentToolPolicy
        {
            ToolName = policy.ToolName,
            ApprovalMode = policy.ApprovalMode
        })];
    }

    private static List<AllowedDirectory> NormalizeDirectories(IEnumerable<AllowedDirectory>? directories, StringComparer nameComparer)
    {
        var labels = new HashSet<string>(nameComparer);
        var result = new List<AllowedDirectory>();
        foreach (var directory in directories ?? [])
        {
            var label = directory.Label?.Trim() ?? string.Empty;
            var value = directory.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new InvalidOperationException("Additional folder labels are required.");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Additional folder '{label}' needs a path.");
            }

            if (!labels.Add(label))
            {
                throw new InvalidOperationException($"Additional folder label '{label}' is duplicated.");
            }

            if (!Enum.IsDefined(directory.Kind))
            {
                throw new InvalidOperationException($"Additional folder '{label}' has an unsupported source kind.");
            }

            if (!Enum.IsDefined(directory.AccessMode))
            {
                throw new InvalidOperationException($"Additional folder '{label}' has an unsupported access mode.");
            }

            result.Add(new AllowedDirectory(value, label, directory.Kind, directory.AccessMode));
        }

        return result;
    }

    private static void ValidateKnownValues(string label, IReadOnlyList<string> values, IReadOnlySet<string>? knownValues)
    {
        if (knownValues is null)
        {
            return;
        }

        foreach (var value in values)
        {
            if (!knownValues.Contains(value))
            {
                throw new InvalidOperationException($"{label} '{value}' is no longer available.");
            }
        }
    }

    private static void ValidateNoManagedTools(IReadOnlyList<string> allowedToolNames)
    {
        foreach (var toolName in allowedToolNames)
        {
            if (!TaskRunScope.IsManagedToolName(toolName))
            {
                continue;
            }

            if (string.Equals(toolName, "search_tools", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Do not put search_tools in allowed_tool_names; use is_tool_search_enabled instead.");
            }

            throw new InvalidOperationException("Library tools are controlled by the Library workspace grant, not allowed_tool_names.");
        }
    }
}
