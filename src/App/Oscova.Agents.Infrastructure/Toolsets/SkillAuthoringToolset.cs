using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.ApiContracts.Skills;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Skills;
using Oscova.Agents.Definitions;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;
using Oscova.Agents.Runtime;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>
/// Lets an approved agent create or update markdown-only skills for future runs.
/// </summary>
public sealed class SkillAuthoringToolset(
    string userId,
    ISkillImportService skillImportService,
    IAgentDefinitionRepository agentDefinitions,
    IAgentCacheInvalidator cacheInvalidator,
    RunContextAccessor runContext,
    ILogger<SkillAuthoringToolset> logger) : IAgentToolset
{
    [DisplayName("create")]
    [AgentTool]
    [Description("Create a markdown-only skill from a complete SKILL.md document and assign it to you.\nRequires user approval.\nThe skill becomes available on your next run.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    public async Task<string> CreateSkillAsync(
        [Description("The complete SKILL.md markdown, including YAML frontmatter with name and description.")] string content,
        CancellationToken cancellationToken = default)
    {
        var authoring = CreateAuthoringContext();
        if (authoring is null)
        {
            throw Failed("No active agent context; cannot create a skill.");
        }

        ImportedSkillResult result;
        try
        {
            var frameworkResult = await skillImportService
                .ImportAgentAuthoredMarkdownAsync(userId, content, authoring, cancellationToken)
                .ConfigureAwait(false);
            result = SkillImportResultMapper.ToImportedSkillResult(frameworkResult);
        }
        catch (SkillImportConflictException)
        {
            throw Failed("A skill with that name already exists.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            logger.LogError(ex, "Agent-authored skill create input was rejected.");
            throw Failed("The skill content is invalid.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Agent-authored skill create failed.");
            throw Failed("The skill could not be saved.");
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            logger.LogError(ex, "Agent-authored skill create failed unexpectedly.");
            throw;
        }

        return await CompleteSafelyAsync("created", result, cancellationToken).ConfigureAwait(false);
    }

    [DisplayName("update")]
    [AgentTool]
    [Description("Replace a previously agent-authored markdown-only skill with a complete SKILL.md document.\nRequires user approval.\nThe updated skill becomes available on your next run.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    public async Task<string> UpdateSkillAsync(
        [Description("The exact skill name to update. Must match the SKILL.md frontmatter name.")] string skillName,
        [Description("The complete replacement SKILL.md markdown, including YAML frontmatter.")] string content,
        CancellationToken cancellationToken = default)
    {
        var authoring = CreateAuthoringContext();
        if (authoring is null)
        {
            throw Failed("No active agent context; cannot update a skill.");
        }

        ImportedSkillResult result;
        try
        {
            var frameworkResult = await skillImportService
                .ReplaceAgentAuthoredMarkdownAsync(userId, skillName, content, authoring, cancellationToken)
                .ConfigureAwait(false);
            result = SkillImportResultMapper.ToImportedSkillResult(frameworkResult);
        }
        catch (SkillAuthorshipException)
        {
            throw Failed("Only an agent-authored skill can be updated with skillauthoring__update.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException)
        {
            logger.LogError(ex, "Agent-authored skill update input was rejected for {SkillName}.", skillName);
            throw Failed("The skill update is invalid or the skill was not found.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Agent-authored skill update failed for {SkillName}.", skillName);
            throw Failed("The skill could not be updated.");
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            logger.LogError(ex, "Agent-authored skill update failed unexpectedly for {SkillName}.", skillName);
            throw;
        }

        return await CompleteSafelyAsync("updated", result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CompleteSafelyAsync(
        string status,
        ImportedSkillResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CompleteAsync(status, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ToolFailureException and not OperationCanceledException)
        {
            logger.LogError(ex, "Agent-authored skill was saved but assignment failed unexpectedly.");
            throw;
        }
    }

    private AgentAuthoredSkillContext? CreateAuthoringContext()
    {
        var context = runContext.Current;
        if (context is null || string.IsNullOrWhiteSpace(context.CurrentAgentId))
        {
            return null;
        }

        return new AgentAuthoredSkillContext(
            context.CurrentAgentId,
            string.IsNullOrWhiteSpace(context.CurrentAgentName) ? context.CurrentAgentId : context.CurrentAgentName,
            DateTimeOffset.UtcNow);
    }

    private async Task<string> CompleteAsync(
        string status,
        ImportedSkillResult result,
        CancellationToken cancellationToken)
    {
        var assignment = await SkillToolsetAssignment.AssignToCurrentAgentAsync(
            userId,
            result.Name,
            agentDefinitions,
            cacheInvalidator,
            runContext,
            logger,
            cancellationToken).ConfigureAwait(false);

        if (!assignment.Success)
        {
            throw new ToolFailureException(
                "The skill was saved but could not be assigned.",
                "Assign it to the agent in Settings.");
        }

        return JsonSerializer.Serialize(new
        {
            status,
            skillName = result.Name,
            description = result.Description,
            assignedToAgent = assignment.AgentName,
            available = "next_run",
            warnings = result.Warnings,
            message = "Saved and assigned. The skill is available the next time this agent starts a run."
        });
    }

    private static ToolFailureException Failed(string reason) => new(reason);
}
