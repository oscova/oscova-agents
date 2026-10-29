using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Conversations;
using Oscova.Agents.Skills;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Builds the display-friendly approval request from a raw tool approval content item.
/// </summary>
public static class ToolApprovalRequestBuilder
{
    private static readonly string ExecCommandName = GetQualifiedToolName(
        typeof(ShellToolset),
        nameof(ShellToolset.ExecCommandAsync));
    private static readonly string StartCommandName = GetQualifiedToolName(
        typeof(ShellToolset),
        nameof(ShellToolset.StartCommandAsync));
    private static readonly string ExecHostCommandName = GetQualifiedToolName(
        typeof(HostShellToolset),
        nameof(HostShellToolset.ExecHostCommandAsync));

    /// <summary>Builds the approval display, preferring a review the owning toolset resolved from trusted data.</summary>
    public static ToolApprovalRequest BuildDisplayRequest(
        ToolApprovalRequestContent request,
        RunContext? runContext = null,
        ToolApprovalReview? trustedReview = null)
    {
        if (request.ToolCall is FunctionCallContent functionCall)
        {
            JsonElement? arguments = functionCall.Arguments is null
                ? null
                : JsonSerializer.SerializeToElement(ToolCallArgumentRedactor.Redact(functionCall.Arguments));

            ToolApprovalReview? review = trustedReview ?? BuildReview(functionCall, runContext);
            return new ToolApprovalRequest
            {
                RequestId = request.RequestId,
                AgentId = runContext?.CurrentAgentId,
                AgentName = runContext?.CurrentAgentName,
                ToolName = functionCall.Name,
                Message = review?.Kind is "shell" or "skill-script"
                    ? review.Description ?? $"Approve execution of '{functionCall.Name}'?"
                    : $"Approve execution of '{functionCall.Name}'?",
                Arguments = arguments,
                Review = review
            };
        }

        return new ToolApprovalRequest
        {
            RequestId = request.RequestId,
            AgentId = runContext?.CurrentAgentId,
            AgentName = runContext?.CurrentAgentName,
            ToolName = "tool",
            Message = "Approve this tool call?"
        };
    }

    private static ToolApprovalReview? BuildReview(FunctionCallContent functionCall, RunContext? runContext)
    {
        ToolApprovalReview? executionReview = BuildExecutionReview(functionCall, runContext);
        if (executionReview is not null)
        {
            return executionReview;
        }

        var (qualifier, tool) = ToolNaming.SplitQualifiedName(functionCall.Name);
        var owner = string.IsNullOrEmpty(qualifier) ? string.Empty : ToolNaming.SplitQualifiedName(qualifier).Tool;
        if (owner == "agenttask" && tool is "create" or "update" or "delete" or "run")
        {
            var scheduleKind = NormalizeReviewToken(TryGetString(functionCall.Arguments, "scheduleKind"));
            var requestedRunMode = NormalizeReviewToken(
                TryGetString(functionCall.Arguments, "toolApprovalRunMode"));

            return new ToolApprovalReview
            {
                Kind = "agent-task",
                Operation = tool,
                TaskName = tool == "create"
                    ? TryGetString(functionCall.Arguments, "name")
                    : TryGetString(functionCall.Arguments, "taskName"),
                NewTaskName = tool == "update"
                    ? TryGetString(functionCall.Arguments, "name")
                    : null,
                TaskAgentName = TryGetString(functionCall.Arguments, "taskAgentName"),
                ScheduleKind = scheduleKind,
                ScheduleValue = GetScheduleValue(functionCall.Arguments, scheduleKind),
                TimeZoneId = TryGetString(functionCall.Arguments, "timeZoneId"),
                RunMode = tool switch
                {
                    "create" => requestedRunMode ?? "auto_reject",
                    "update" => requestedRunMode ?? NormalizeReviewToken(
                        TryGetString(functionCall.Arguments, "currentToolApprovalRunMode")),
                    _ => null
                }
            };
        }

        if (owner != "skillauthoring" || tool is not ("create" or "update"))
        {
            return null;
        }

        var skillName = TryGetString(functionCall.Arguments, "skillName");
        var content = TryGetString(functionCall.Arguments, "content");
        var description = string.Empty;

        if (!string.IsNullOrWhiteSpace(content) &&
            SkillFrontmatter.TryRead(content, out var frontmatter))
        {
            frontmatter.TryGetValue("name", out var parsedName);
            frontmatter.TryGetValue("description", out description);
            if (!string.IsNullOrWhiteSpace(skillName) &&
                !string.IsNullOrWhiteSpace(parsedName) &&
                !string.Equals(skillName, parsedName, StringComparison.Ordinal))
            {
                skillName = $"{skillName} (frontmatter: {parsedName})";
            }
            else
            {
                skillName = string.IsNullOrWhiteSpace(skillName) ? parsedName : skillName;
            }
        }

        return new ToolApprovalReview
        {
            Kind = "skill-authoring",
            Operation = tool,
            SkillName = skillName,
            Description = description
        };
    }

    private static ToolApprovalReview? BuildExecutionReview(
        FunctionCallContent functionCall,
        RunContext? runContext)
    {
        bool skillScript = string.Equals(
            functionCall.Name,
            AgentSkillsProvider.RunSkillScriptToolName,
            StringComparison.Ordinal);
        bool explicitHost = string.Equals(functionCall.Name, ExecHostCommandName, StringComparison.Ordinal);
        string? operation = functionCall.Name switch
        {
            _ when string.Equals(functionCall.Name, ExecCommandName, StringComparison.Ordinal) => "exec_command",
            _ when string.Equals(functionCall.Name, StartCommandName, StringComparison.Ordinal) => "start_command",
            _ when explicitHost => HostShellToolset.ToolName,
            _ when skillScript => AgentSkillsProvider.RunSkillScriptToolName,
            _ => null
        };
        if (operation is null)
        {
            return null;
        }

        string target = explicitHost
            ? "host"
            : runContext?.ExecutionProviderId switch
            {
                "host" => "host",

                null => "disabled",
                _ => runContext?.ExecutionProviderId ?? "unavailable"
            };
        if (target == "host" && runContext?.ProjectId is not null) { target = "project"; }

        string description = target switch
        {
            "project" => "Runs on the selected project's device with that device account's operating-system permissions.",
            "host" => "Runs on the machine running Oscova with its operating-system account's permissions.",
            "disabled" => "Shell execution is disabled.",
            _ => $"Runs in the selected execution environment '{target}'."
        };
        if (skillScript)
        {
            description += " Approval runs the whole script. Commands inside it are not approved separately.";
        }

        return new ToolApprovalReview
        {
            Kind = skillScript ? "skill-script" : "shell",
            Operation = operation,
            RunMode = target,
            SkillName = skillScript ? TryGetString(functionCall.Arguments, "skillName") : null,
            Description = description
        };
    }

    private static string GetQualifiedToolName(Type toolsetType, string methodName)
    {
        MethodInfo method = toolsetType.GetMethod(methodName)
            ?? throw new InvalidOperationException($"Tool method '{methodName}' was not found.");
        string baseName = method.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? method.Name;
        return ToolNaming.BuildToolsetQualifiedName(
            null,
            ToolNaming.GetToolsetOwner(toolsetType),
            ToolNaming.NormalizeToolName(baseName));
    }

    /// <summary>Reads one tool-call argument as text, whether it arrived as a string or JSON.</summary>
    public static string? TryGetString(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static string? NormalizeReviewToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant().Replace('-', '_');

    private static string? GetScheduleValue(
        IDictionary<string, object?>? arguments,
        string? scheduleKind)
    {
        return scheduleKind switch
        {
            "interval" => TryGetString(arguments, "intervalMinutes"),
            "one_shot" => TryGetString(arguments, "runAt"),
            "cron" => TryGetString(arguments, "cronExpression"),
            _ => null
        };
    }
}
