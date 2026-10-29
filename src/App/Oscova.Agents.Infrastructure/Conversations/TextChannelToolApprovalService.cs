using Oscova.Agents.Conversations;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Mcp;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>Renders approval prompts and parses replies through the shared plugin transport.</summary>
public sealed class TextChannelToolApprovalService : ITextChannelToolApprovalService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    public const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions PromptJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ApproveKeywords = ["yes", "y", "approve", "approved", "ok", "okay"];
    private static readonly string[] RejectKeywords = ["no", "n", "reject", "rejected", "deny", "denied", "cancel"];

    private readonly PluginTextElicitationTransport _transport;
    private readonly RunContextAccessor _runContextAccessor;
    private readonly ILogger<TextChannelToolApprovalService> _logger;

    public TextChannelToolApprovalService(
        PluginTextElicitationTransport transport,
        RunContextAccessor runContextAccessor,
        ILogger<TextChannelToolApprovalService> logger)
    {
        _transport = transport;
        _runContextAccessor = runContextAccessor;
        _logger = logger;
    }

    public async Task<ToolApprovalSubmission> HandleApprovalAsync(
        ToolApprovalRequestContent request,
        ToolApprovalRequestContent displaySource,
        ToolApprovalReview? review,
        PluginElicitationOrigin origin,
        CancellationToken cancellationToken)
    {
        var displayRequest = ToolApprovalRequestBuilder.BuildDisplayRequest(displaySource, _runContextAccessor.Current, review);
        var prompt = BuildPrompt(displayRequest);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var reply = await _transport.SendPromptAndAwaitReplyAsync(origin, prompt, timeoutCts.Token).ConfigureAwait(false);
                if (reply is null)
                {
                    return Cancelled(request);
                }

                var trimmed = reply.Trim().ToLowerInvariant();
                if (Array.IndexOf(ApproveKeywords, trimmed) >= 0)
                {
                    // Text channels keep the raw "yes" reply visible, so we send an explicit approval acknowledgement before the run resumes.
                    await _transport.SendAcknowledgementAsync(origin, "Approved.", timeoutCts.Token).ConfigureAwait(false);
                    var response = request.CreateResponse(approved: true, "Approved by user");
                    return new ToolApprovalSubmission(response, null);
                }

                if (Array.IndexOf(RejectKeywords, trimmed) >= 0)
                {
                    await _transport.SendAcknowledgementAsync(origin, "Rejected.", timeoutCts.Token).ConfigureAwait(false);
                    var response = request.CreateResponse(approved: false, "Rejected by user");
                    return new ToolApprovalSubmission(response, null);
                }

                if (attempt < MaxAttempts)
                {
                    prompt = "Reply 'yes' or 'no'.";
                }
            }

            await _transport.SendAcknowledgementAsync(origin, "Rejected (no valid reply).", timeoutCts.Token).ConfigureAwait(false);
            return Cancelled(request);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Text tool approval on channel {Channel} timed out after {Timeout}.", origin.Key.ChannelId, DefaultTimeout);
            return Cancelled(request);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Text tool approval on channel {Channel} failed.", origin.Key.ChannelId);
            return Cancelled(request);
        }
    }

    private static string BuildPrompt(ToolApprovalRequest displayRequest)
    {
        var prompt = $"Tool approval required: {displayRequest.Message}";
        if (displayRequest.Review?.Kind == "skill-authoring")
        {
            prompt +=
                $"\nOperation: {displayRequest.Review.Operation}" +
                $"\nSkill: {displayRequest.Review.SkillName ?? "-"}" +
                $"\nDescription: {displayRequest.Review.Description ?? "-"}";
        }
        else if (displayRequest.Review?.Kind == "agent-task")
        {
            prompt += BuildTaskReview(displayRequest.Review);
        }
        else if (displayRequest.Review?.Kind is "shell" or "skill-script")
        {
            prompt += $"\nDescription: {displayRequest.Review.Description}";
        }
        else if (displayRequest.Review?.Kind == "registry-install")
        {
            prompt += BuildRegistryInstallReview(displayRequest.Review);
        }

        // Keep arguments inspectable, but no longer make them the only review surface.
        if (displayRequest.Arguments is { } arguments)
        {
            prompt += $"\nArguments: {JsonSerializer.Serialize(arguments, PromptJsonOptions)}";
        }

        return $"{prompt}\nReply 'yes' to approve or 'no' to reject.";
    }

    private static string BuildRegistryInstallReview(ToolApprovalReview review)
    {
        var lines = new List<string>
        {
            review.Operation == "skill" ? $"Skill: {review.SkillName ?? "-"}" : $"MCP server: {review.ServerName ?? "-"}",
            $"Publisher: {review.Publisher ?? "-"}"
        };
        if (review.Version is { } version) { lines.Add($"Version: {version}"); }

        lines.Add($"Source: {review.SourceRepository ?? "-"}");
        if (review.Transport is { } transport) { lines.Add($"Transport: {transport}"); }
        // A stdio server runs a local command, while other transports only connect to an endpoint.
        if (review.Command is { } command) { lines.Add($"{(review.Transport == nameof(McpTransportType.Stdio) ? "Runs" : "Connects to")}: {command}"); }

        if (review.NetworkScope is { } scope)
        {
            lines.Add("Network: " + scope switch
            {
                "None" => "No network access",
                "Incidental" => "Occasional network use",
                _ => "Sends data to outside services"
            });
        }

        return "\n" + string.Join('\n', lines);
    }

    private static string BuildTaskReview(ToolApprovalReview review)
    {
        var taskReview =
            $"\nOperation: {review.Operation}" +
            $"\nTask: {review.TaskName ?? "-"}";

        if (!string.IsNullOrWhiteSpace(review.NewTaskName))
        {
            taskReview += $"\nNew name: {review.NewTaskName}";
        }

        if (!string.IsNullOrWhiteSpace(review.TaskAgentName))
        {
            taskReview += $"\nAgent: {review.TaskAgentName}";
        }

        if (!string.IsNullOrWhiteSpace(review.ScheduleKind))
        {
            taskReview += $"\nSchedule: {FormatTaskSchedule(review)}";
            if (review.ScheduleKind is "one_shot" or "cron")
            {
                taskReview += $"\nTime zone: {review.TimeZoneId ?? "configured user time zone"}";
            }
        }

        if (!string.IsNullOrWhiteSpace(review.RunMode))
        {
            taskReview += review.RunMode == "auto_approve"
                ? "\nRestricted tools: auto-approved during runs" +
                  "\nWARNING: Approval-required tools may run unattended during this task. Host shell commands can change the machine running Oscova."
                : "\nRestricted tools: auto-rejected during runs";
        }

        return taskReview;
    }

    private static string FormatTaskSchedule(ToolApprovalReview review)
    {
        return review.ScheduleKind switch
        {
            "interval" => $"every {review.ScheduleValue ?? "-"} minutes",
            "one_shot" => $"once at {review.ScheduleValue ?? "-"}",
            "cron" => $"cron '{review.ScheduleValue ?? "-"}'",
            _ => review.ScheduleKind ?? "-"
        };
    }

    private static ToolApprovalSubmission Cancelled(ToolApprovalRequestContent request)
    {
        var response = request.CreateResponse(approved: false, "Cancelled");
        return new ToolApprovalSubmission(response, null);
    }
}
