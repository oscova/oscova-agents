using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core.ApiContracts.Hooks;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Host.Server.Security;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Persistence;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AgentsServerAuthenticationDefaults.HookApiKeyScheme)]
[Route("api/hooks")]
[IgnoreAntiforgeryToken]
public sealed class HooksController(
    UserManager<ApplicationUser> userManager,
    Oscova.Agents.Secrets.IContentProtector contentProtector,
    IAgentTaskRepository taskRepository,
    AgentTaskSchedulerService taskScheduler,
    ILogger<HooksController> logger) : ControllerBase
{
    [HttpPost("tasks/{slug}/runs")]
    public async Task<ActionResult<TriggerTaskHookResponse>> TriggerTask(
        string slug,
        [FromBody] TriggerTaskHookRequest? request,
        CancellationToken ct)
    {
        var normalizedSlug = Slugs.ForTaskTrigger(slug);
        if (!HasTaskRunScope() || !IsAllowedTaskSlug(normalizedSlug))
        {
            return Forbid();
        }

        var task = await GetTaskBySlugAsync(normalizedSlug, ct);
        if (task.Result is not null)
        {
            return task.Result;
        }

        if (task.Value?.IsEnabled != true || !task.Value.ExternalTrigger.IsEnabled)
        {
            return NotFound();
        }

        IReadOnlyDictionary<string, System.Text.Json.JsonElement> parameters;
        try
        {
            parameters = TaskExternalTriggerValidator.ValidateParameters(
                task.Value,
                request?.Parameters is null
                    ? new Dictionary<string, System.Text.Json.JsonElement>()
                    : new Dictionary<string, System.Text.Json.JsonElement>(request.Parameters, StringComparer.OrdinalIgnoreCase));
        }
        catch (TaskExternalTriggerValidationException ex)
        {
            return BadRequest(GetExternalTriggerValidationMessage(ex));
        }

        var outcome = await taskScheduler.TriggerTaskAsync(
            task.Value,
            TaskTriggerSource.ExternalWebhook,
            new Dictionary<string, System.Text.Json.JsonElement>(parameters, StringComparer.OrdinalIgnoreCase),
            ct);
        var response = BuildResponse(normalizedSlug, task.Value.Id, outcome);
        return outcome.Started
            ? Accepted(response.StatusUrl, response)
            : Conflict(response);
    }

    [HttpGet("tasks/{slug}/runs/{runId}")]
    public async Task<ActionResult<TaskHookRunStatus>> GetTaskRunStatus(string slug, string runId, CancellationToken ct)
    {
        var normalizedSlug = Slugs.ForTaskTrigger(slug);
        if (!HasTaskRunScope() || !IsAllowedTaskSlug(normalizedSlug))
        {
            return Forbid();
        }

        var task = await GetTaskBySlugAsync(normalizedSlug, ct);
        if (task.Result is not null)
        {
            return task.Result;
        }

        if (task.Value is null)
        {
            return NotFound();
        }

        if (!contentProtector.IsAvailable(task.Value.UserId))
        {
            return Conflict("Unlock your Vault Password before continuing.");
        }

        AgentTaskRun? run = null;
        if (taskScheduler.TryGetLastRun(task.Value.UserId, task.Value.Id, out var trackedRun) &&
            trackedRun is not null &&
            string.Equals(trackedRun.Id, runId, StringComparison.OrdinalIgnoreCase))
        {
            run = trackedRun;
        }

        run ??= await taskRepository.GetRunMetadataAsync(task.Value.UserId, task.Value.Id, runId, ct);
        return run is null ? NotFound() : Ok(MapRunStatus(run, includeContent: false));
    }

    private async Task<ActionResult<AgentTaskDefinition?>> GetTaskBySlugAsync(string normalizedSlug, CancellationToken ct)
    {
        // Resolve the owner before reading task definitions, which remain encrypted while locked.
        var userIds = await userManager.Users.Select(user => user.Id).ToListAsync(ct);
        if (userIds.Any(userId =>
                string.Equals(Slugs.ForUserTaskTrigger(userId, normalizedSlug), normalizedSlug, StringComparison.OrdinalIgnoreCase) &&
                !contentProtector.IsAvailable(userId)))
        {
            return Conflict("Unlock your Vault Password before continuing.");
        }

        try
        {
            return await taskRepository.GetTaskByExternalTriggerSlugAsync(normalizedSlug, ct);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "External task trigger slug '{Slug}' is ambiguous.", normalizedSlug);
            return Conflict("External task trigger slug is not unique.");
        }
    }

    private static string GetExternalTriggerValidationMessage(
        TaskExternalTriggerValidationException exception)
    {
        return exception.FailureKind switch
        {
            TaskExternalTriggerValidationFailureKind.TriggerDisabled
                => "External triggering is not enabled for this task.",
            TaskExternalTriggerValidationFailureKind.UnknownParameter
                => $"Unknown external trigger parameter '{exception.ParameterName}'.",
            TaskExternalTriggerValidationFailureKind.ParameterKindMismatch
                => $"External trigger parameter '{exception.ParameterName}' must be {exception.ExpectedKind!.Value.ToString().ToLowerInvariant()}.",
            TaskExternalTriggerValidationFailureKind.RequiredParameterMissing
                => $"External trigger parameter '{exception.ParameterName}' is required.",
            _ => throw new ArgumentOutOfRangeException(nameof(exception))
        };
    }

    private bool HasTaskRunScope()
    {
        return User.Claims.Any(claim =>
            string.Equals(claim.Type, HookApiKeyAuthenticationHandler.ScopeClaim, StringComparison.Ordinal) &&
            string.Equals(claim.Value, HookSettingsValidator.TasksRunScope, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsAllowedTaskSlug(string slug)
    {
        return User.Claims.Any(claim =>
            string.Equals(claim.Type, HookApiKeyAuthenticationHandler.AllowedTaskSlugClaim, StringComparison.Ordinal) &&
            string.Equals(claim.Value, slug, StringComparison.OrdinalIgnoreCase));
    }

    private static TriggerTaskHookResponse BuildResponse(string slug, string taskId, TaskTriggerOutcome outcome)
    {
        var runId = outcome.RunId ?? string.Empty;
        return new TriggerTaskHookResponse
        {
            TaskId = taskId,
            RunId = runId,
            Status = outcome.Status,
            StatusUrl = "/" + ApiRoutes.Hooks.TaskRunStatus(slug, runId),
            Message = outcome.Message
        };
    }

    private static TaskHookRunStatus MapRunStatus(AgentTaskRun run, bool includeContent)
    {
        return new TaskHookRunStatus
        {
            TaskId = run.TaskId,
            RunId = run.Id,
            Status = run.Status,
            ResultSummary = includeContent ? run.ResultSummary : null,
            Error = includeContent ? run.Error : null,
            TriggerSource = run.TriggerSource,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt
        };
    }
}
