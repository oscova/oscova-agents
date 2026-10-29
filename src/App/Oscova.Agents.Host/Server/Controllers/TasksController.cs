using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Tasks;
using Oscova.Agents.Core.ApiContracts.Prompts;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Tasks;
using Oscova.Agents.Infrastructure.Tasks;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Skills;
using Oscova.Agents.Tasks;
using System.Text.Json;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Oscova.Agents.Host.Server.Security.RequireUnlockedVault]
[Authorize]
[Route("api/tasks")]
[IgnoreAntiforgeryToken]
public sealed class TasksController(
    UserServiceManager userServiceManager,
    IAgentTaskRepository taskRepository,
    IAgentTaskCatalog agentTaskCatalog,
    AgentTaskSchedulerService taskScheduler,
    IEgressAuditStore egressAuditStore,
    IUserDirectoryService userDirectoryService,
    IAgentCatalog agentCatalog)
    : UserScopeControllerBase(userServiceManager)
{
    /// <summary>Returns an owned task's selection and its actual contribution default.</summary>
    [HttpGet("{taskId}/instruction")]
    public async Task<ActionResult<PromptDocument>> GetInstruction(string taskId, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        var task = await taskRepository.GetTaskAsync(scope.UserId, taskId, ct);
        if (task is null) { return NotFound(); }

        return Ok(new PromptDocument
        {
            Content = task.Instruction,
            Mode = task.InstructionMode,
            DefaultContent = scope.GetTaskInstructionDefault(task),
            FileName = "instruction.md"
        });
    }

    [HttpGet]
    public async Task<ActionResult<TaskConfiguration>> Get(
        [FromQuery] bool includeReservedTasks = true,
        CancellationToken ct = default)
    {
        var userScope = await GetUserScopeAsync(ct);
        var appService = userScope.AppService;
        var taskCatalog = TaskRunScopeValidationCatalogFactory.Create(userScope, userDirectoryService);
        // A missing catalogue must fail closed instead of exposing the foreground tool list.
        IReadOnlySet<string> taskToolNames = taskCatalog.ToolNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userId = userScope.UserId;
        var tasks = (await taskRepository.GetTasksByUserAsync(userId, ct))
            .Where(task => includeReservedTasks || !WellKnownTasks.ReservedFolderKeys.Contains(
                Path.GetFileName(agentTaskCatalog.TryGetTaskDirectory(task.Id)),
                StringComparer.OrdinalIgnoreCase))
            .ToList();
        var latestRuns = (await taskRepository.GetLatestRunsAsync(tasks.Select(task => task.Id), ct))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
        var instructions = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            var defaultInstruction = userScope.GetTaskInstructionDefault(task);
            // Keep display text separate from the saved custom body used by the editor.
            instructions[task.Id] = task.InstructionSource is null ? task.Instruction
                : defaultInstruction is null && task.InstructionMode != InstructionSelection.Replace ? null
                : InstructionSelection.Select(task.InstructionMode, task.Instruction, defaultInstruction);
            if (taskScheduler.TryGetLastRun(userId, task.Id, out var trackedRun) && trackedRun is not null)
            {
                latestRuns[task.Id] = trackedRun;
            }

            if (latestRuns.TryGetValue(task.Id, out var latest))
            {
                latestRuns[task.Id] = await RedactRunAsync(userScope, latest, ct);
            }
        }

        return Ok(new TaskConfiguration
        {
            Tasks = tasks,
            Instructions = instructions,
            LatestRuns = latestRuns,
            RunningTaskIds = [.. tasks
                .Where(task => taskScheduler.IsTaskRunning(userId, task.Id))
                .Select(task => task.Id)],
            AvailableAgents =
            [
                new SelectableAgent
                {
                    Id = appService.MainAgentDefinition.Id,
                    Name = appService.MainAgentDefinition.Name
                },
                .. appService.CustomAgentDefinitions
                    .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(agent => new SelectableAgent
                    {
                        Id = agent.Id,
                        Name = agent.Name
                    })
            ],
            AvailableAgentDefinitions =
            [
                appService.MainAgentDefinition,
                .. appService.CustomAgentDefinitions.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            ],
            ExposableAgents = [.. appService.CustomAgentDefinitions
                .Where(agent => agent.IsExposedAsTool)
                .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
                .Select(agent => new AgentReference
                {
                    Id = agent.Id,
                    Name = agent.Name,
                    Description = agent.Description
                })],
            ToolGroups = GetToolGroups(appService, taskToolNames),
            AvailableSkills = GetAvailableSkills(userScope),
            AvailableWorkspaces = [.. userDirectoryService.ListWorkspaces(userScope.UserId)],
            AvailableModelProviders = ChatModelProviderSelection.GetSelectableProviders(userScope).ToList(),
            UserTimeZoneId = userScope.UiSettings.TimeZoneId
        });
    }

    [HttpGet("{taskId}")]
    public async Task<ActionResult<AgentTaskDefinition>> GetTask(string taskId, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var task = await taskRepository.GetTaskAsync(userScope.UserId, taskId, ct);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpGet("{taskId}/runs")]
    public async Task<ActionResult<IReadOnlyList<AgentTaskRun>>> GetRuns(string taskId, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var userScope = await GetUserScopeAsync(ct);
        if (await taskRepository.GetTaskAsync(userScope.UserId, taskId, ct) is null)
        {
            return NotFound();
        }

        var runs = await taskRepository.GetRunsAsync(userScope.UserId, taskId, Math.Clamp(limit, 1, 100), ct);

        if (taskScheduler.TryGetLastRun(userScope.UserId, taskId, out var trackedRun) && trackedRun is not null)
        {
            runs = [trackedRun, .. runs.Where(run => run.Id != trackedRun.Id)];
        }

        var snapshots = new List<AgentTaskRun>(runs.Count);
        foreach (var run in runs) { snapshots.Add(await RedactRunAsync(userScope, run, ct)); }

        return Ok(snapshots);
    }

    [HttpGet("{taskId}/runs/{runId}")]
    public async Task<ActionResult<AgentTaskRun>> GetRun(string taskId, string runId, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        if (await taskRepository.GetTaskAsync(userScope.UserId, taskId, ct) is null)
        {
            return NotFound();
        }

        if (taskScheduler.TryGetLastRun(userScope.UserId, taskId, out var trackedRun) &&
            trackedRun is not null &&
            string.Equals(trackedRun.Id, runId, StringComparison.OrdinalIgnoreCase))
        {
            return Ok(await RedactRunAsync(userScope, trackedRun, ct));
        }

        var run = await taskRepository.GetRunAsync(userScope.UserId, taskId, runId, ct);
        return run is null ? NotFound() : Ok(await RedactRunAsync(userScope, run, ct));
    }

    /// <summary>Deletes one saved run. The newest saved run anchors the next due time, so it cannot be deleted.</summary>
    [HttpDelete("{taskId}/runs/{runId}")]
    public async Task<IActionResult> DeleteRun(string taskId, string runId, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var latest = await taskRepository.GetLatestRunMetadataAsync(userScope.UserId, taskId, ct);
        if (latest is null)
        {
            return NotFound();
        }

        if (string.Equals(latest.Id, runId, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("The newest run cannot be deleted because it sets when the task runs next.");
        }

        if (!await taskRepository.DeleteRunAsync(userScope.UserId, taskId, runId, ct))
        {
            return NotFound();
        }

        taskScheduler.ForgetLastRun(userScope.UserId, taskId, runId);
        return NoContent();
    }

    /// <summary>Builds diagnostic evidence without changing the task's resumable execution state.</summary>
    private static async Task<AgentTaskRun> RedactRunAsync(UserServiceScope scope, AgentTaskRun run, CancellationToken ct)
    {
        var fields = await scope.RedactionService.RedactArgumentsAsync(new Dictionary<string, object?>
        {
            [nameof(run.SystemInstruction)] = run.SystemInstruction,
            [nameof(run.ResultSummary)] = run.ResultSummary,
            [nameof(run.Error)] = run.Error,
            [nameof(run.SessionElement)] = run.SessionElement,
            [nameof(run.TriggerParameters)] = run.TriggerParameters
        }, scope.UserId, ct);
        var messages = run.GetLiveMessagesSnapshot();
        return new AgentTaskRun
        {
            Id = run.Id, TaskId = run.TaskId, UserId = run.UserId, Status = run.Status,
            StartedAt = run.StartedAt, CompletedAt = run.CompletedAt, TokensUsed = run.TokensUsed,
            TriggerSource = run.TriggerSource,
            SystemInstruction = (string?)fields[nameof(run.SystemInstruction)],
            ResultSummary = (string?)fields[nameof(run.ResultSummary)],
            Error = (string?)fields[nameof(run.Error)],
            SessionElement = run.SessionElement is null ? null : JsonSerializer.SerializeToElement(fields[nameof(run.SessionElement)]),
            TriggerParameters = JsonSerializer.SerializeToElement(fields[nameof(run.TriggerParameters)])
                .Deserialize<Dictionary<string, JsonElement>>()!,
            LiveMessages = messages is null ? null :
                (await scope.RedactionService.RedactMessagesAsync(messages, scope.UserId, ct)).ToList()
        };
    }

    [HttpGet("{taskId}/runs/{runId}/egress")]
    public async Task<ActionResult<IReadOnlyList<EgressAuditEntry>>> GetRunEgress(string taskId, string runId, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var labels = UserLabelMap.Create(userScope, agentCatalog);
        var lines = await egressAuditStore.ReadAllAsync(userScope.UserId, ct);
        return Ok(lines
            .Where(line =>
                string.Equals(line.TaskId, taskId, StringComparison.Ordinal) &&
                string.Equals(line.RunId, runId, StringComparison.Ordinal))
            .Select(line => labels.Label(line))
            .ToList());
    }

    [HttpPost]
    public async Task<ActionResult<AgentTaskDefinition>> Create([FromBody] AgentTaskDefinition request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var (task, error) = await userScope.TaskManagementService.SaveAsync(
            request,
            existing: null,
            createdByAgentId: null,
            ct);
        return task is null ? BadRequest(error) : Ok(task);
    }

    [HttpPut("{taskId}")]
    public async Task<ActionResult<AgentTaskDefinition>> Save(string taskId, [FromBody] AgentTaskDefinition request,
        CancellationToken ct, [FromQuery] bool preserveEnabled = false)
    {
        var userScope = await GetUserScopeAsync(ct);
        var existing = await taskRepository.GetTaskAsync(userScope.UserId, taskId, ct);
        if (existing is null)
        {
            return NotFound();
        }

        var (task, error) = await userScope.TaskManagementService.SaveAsync(
            request,
            existing,
            createdByAgentId: null,
            ct, preserveEnabled: preserveEnabled);
        return task is null ? BadRequest(error) : Ok(task);
    }

    [HttpPut("{taskId}/enabled")]
    public async Task<ActionResult<AgentTaskDefinition>> SetEnabled(string taskId, [FromBody] bool isEnabled, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var task = await userScope.TaskManagementService.SetEnabledAsync(taskId, isEnabled, ct);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpDelete("{taskId}")]
    public async Task<IActionResult> Delete(string taskId, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        await userScope.TaskManagementService.DeleteAsync(taskId, ct);
        return NoContent();
    }

    [HttpPost("{taskId}/trigger")]
    public async Task<ActionResult<TriggerTaskResult>> Trigger(string taskId, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var task = await taskRepository.GetTaskAsync(userScope.UserId, taskId, ct);
        if (task is null)
        {
            return NotFound();
        }

        var outcome = await userScope.TaskManagementService.TriggerAsync(task, ct);
        var result = new TriggerTaskResult
        {
            Started = outcome.Started,
            RunId = outcome.RunId,
            Message = outcome.Message
        };

        return !outcome.Started && string.IsNullOrWhiteSpace(outcome.RunId)
            ? Conflict(result)
            : Ok(result);
    }

    private static List<ToolGroup> GetToolGroups(
        AppService appService,
        IReadOnlySet<string> availableToolNames)
    {
        return [.. appService.AllTools
            .Where(tool => availableToolNames.Contains(tool.Name))
            .GroupBy(tool => appService.CrewService.Tools.GetServerName(tool), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ToolGroup
            {
                ServerName = group.Key,
                Tools = [.. group
                    .OrderBy(tool => appService.CrewService.Tools.GetOriginalName(tool), StringComparer.OrdinalIgnoreCase)
                    .Select(tool => new ToolDefinition
                    {
                        QualifiedName = tool.Name,
                        OriginalName = appService.CrewService.Tools.GetOriginalName(tool),
                        Description = tool.Description,
                        DefaultApprovalMode = appService.CrewService.Tools.GetDefaultApprovalMode(tool.Name),
                        NetworkScope = appService.CrewService.Tools.GetNetworkScope(tool.Name)
                    })]
            })];
    }

    private List<SkillDefinition> GetAvailableSkills(UserServiceScope userScope)
    {
        var discoverer = new SkillDiscoverer();
        var skills = discoverer.Discover(userDirectoryService.GetSkillsDirectoryPath(userScope.UserId));

        return [.. skills
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(skill => new SkillDefinition
            {
                Name = skill.Name,
                Description = skill.Description
            })];
    }

}
