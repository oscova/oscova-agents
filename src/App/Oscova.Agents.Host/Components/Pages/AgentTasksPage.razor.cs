using Cronos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using System.Globalization;
using System.Net;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Core.ApiContracts.Tasks;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Definitions;
using Oscova.Agents.Host.Components.Dialogs;
using Oscova.Agents.Host.Components.Tasks;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Services.Realtime;
using Oscova.Agents.Host.Services.UI;
using Oscova.Agents.Tasks;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Host.Components.Pages;

public partial class AgentTasksPage : ComponentBase, IDisposable
{
    [Inject] private ManagementApiClient ManagementApiClient { get; set; } = default!;
    [Inject] private SystemHubClient SystemHubClient { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private IToastService ToastService { get; set; } = default!;
    [Inject] private IStringLocalizer<CommonText> CommonLoc { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private bool _isLoading = true;
    private List<AgentTaskDefinition> _tasks = [];
    private IReadOnlyDictionary<string, string?> _instructions = new Dictionary<string, string?>();
    private Dictionary<string, AgentTaskRun?> _lastRuns = new();
    private HashSet<string> _runningTaskIds = [];
    private List<SelectableAgent> _availableAgents = [];
    private List<AgentDefinition> _availableAgentDefinitions = [];
    private List<ToolGroup> _toolGroups = [];
    private List<AgentReference> _exposableAgents = [];
    private List<SkillDefinition> _availableSkills = [];
    private List<string> _availableWorkspaces = [];
    private HashSet<string> _availableAgentIds = new(StringComparer.OrdinalIgnoreCase);
    private List<SelectableModelProvider> _availableModelProviders = [];
    private string _userTimeZoneId = string.Empty;
    private TimeZoneInfo _userTimeZone = TimeZoneInfo.Local;
    private string? _selectedTaskId;
    private string? _requestedTaskId;
    private string? _requestedRunId;
    private bool _shouldOpenRequestedTask;
    private string _searchText = string.Empty;
    private TaskStatusFilter _statusFilter = TaskStatusFilter.All;
    private bool _showInstructions;

    private List<AgentTaskDefinition> FilteredTasks => _tasks.Where(MatchesFilter).ToList();
    private int RunningTaskCount => _tasks.Count(task => GetLastRun(task.Id)?.Status == AgentTaskStatus.Running);
    private int CompletedTaskCount => _tasks.Count(task => GetLastRun(task.Id)?.Status == AgentTaskStatus.Completed);
    private int NeedsAttentionTaskCount => _tasks.Count(task => NeedsAttention(task.Id));

    protected override async Task OnInitializedAsync()
    {
        SystemHubClient.OnTaskStatusUpdatedAsync = OnTaskStatusUpdatedAsync;
        NavigationManager.LocationChanged += OnLocationChanged;
        ApplyNavigationStateFromQuery();
        await TryRunPageCommandAsync(async () =>
        {
            await SystemHubClient.EnsureConnectedAsync();
            await LoadTasksAsync();
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_shouldOpenRequestedTask || string.IsNullOrWhiteSpace(_selectedTaskId))
        {
            return;
        }

        var requestedTask = _tasks.FirstOrDefault(task => string.Equals(task.Id, _requestedTaskId, StringComparison.Ordinal));
        if (requestedTask is null)
        {
            return;
        }

        _shouldOpenRequestedTask = false;
        await OpenTaskDetailsAsync(requestedTask);
    }

    private async Task LoadTasksAsync()
    {
        _isLoading = true;
        try
        {
            var configuration = await ManagementApiClient.GetTaskConfigurationAsync();
            _tasks = configuration.Tasks.ToList();
            _instructions = configuration.Instructions;
            _lastRuns = configuration.LatestRuns.ToDictionary(kvp => kvp.Key, kvp => (AgentTaskRun?)kvp.Value);
            _runningTaskIds = configuration.RunningTaskIds.ToHashSet(StringComparer.Ordinal);
            _availableAgents = configuration.AvailableAgents.ToList();
            _availableAgentDefinitions = configuration.AvailableAgentDefinitions.ToList();
            _toolGroups = configuration.ToolGroups.ToList();
            _exposableAgents = configuration.ExposableAgents.ToList();
            _availableSkills = configuration.AvailableSkills.ToList();
            _availableWorkspaces = configuration.AvailableWorkspaces.ToList();
            _availableAgentIds = _availableAgents.Select(agent => agent.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _availableModelProviders = configuration.AvailableModelProviders.ToList();
            _userTimeZoneId = configuration.UserTimeZoneId;
            _userTimeZone = TimeZoneResolver.ResolveOrLocal(_userTimeZoneId);

            await EnsureSelectionAsync();
        }
        finally
        {
            // A failed read must leave Refresh available for recovery.
            _isLoading = false;
        }
    }

    private async Task OpenCreateDialog()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["AvailableAgents"] = _availableAgents,
            ["AvailableAgentDefinitions"] = _availableAgentDefinitions,
            ["AvailableTasks"] = BuildSelectableTasks(),
            ["ToolGroups"] = _toolGroups,
            ["ExposableAgents"] = _exposableAgents,
            ["AvailableSkills"] = _availableSkills,
            ["AvailableWorkspaces"] = _availableWorkspaces,
            ["AvailableModelProviders"] = _availableModelProviders,
            ["UserTimeZoneId"] = _userTimeZoneId,
            ["SaveTaskAsync"] = (Func<AgentTaskDefinition, Task<bool>>)(draft =>
                TryRunPageCommandAsync(() => ManagementApiClient.CreateTaskAsync(draft)))
        };

        var result = await DialogService.ShowAsync<CreateAgentTaskDialog, AgentTaskDefinition>(parameters);
        if (result.IsOk && result.Data is not null)
        {
            await TryRunPageCommandAsync(LoadTasksAsync);
            await SelectTaskAsync(result.Data.Id);
            ToastService.Show(CommonLoc.GetOrDefault(CommonTextKeys.TaskCreatedSuccessfully, "Task created successfully."), ToastType.Success);
        }
    }

    private async Task OpenEditDialog(AgentTaskDefinition task)
    {
        // A run may have paused the task while its details dialog was open.
        AgentTaskDefinition? current = null;
        if (!await TryRunPageCommandAsync(async () => current = await ManagementApiClient.GetTaskAsync(task.Id)) || current is null)
        {
            return;
        }

        task = current;
        var parameters = new Dictionary<string, object?>
        {
            { "Task", task },
            { "AvailableAgents", _availableAgents },
            { "AvailableAgentDefinitions", _availableAgentDefinitions },
            { "AvailableTasks", BuildSelectableTasks() },
            { "ToolGroups", _toolGroups },
            { "ExposableAgents", _exposableAgents },
            { "AvailableSkills", _availableSkills },
            { "AvailableWorkspaces", _availableWorkspaces },
            { "AvailableModelProviders", _availableModelProviders },
            { "UserTimeZoneId", _userTimeZoneId },
            { "SaveTaskAsync", (Func<AgentTaskDefinition, Task<bool>>)(draft =>
                TryRunPageCommandAsync(() => ManagementApiClient.SaveTaskAsync(draft,
                    preserveEnabled: draft.IsEnabled == task.IsEnabled))) }
        };

        var result = await DialogService.ShowAsync<CreateAgentTaskDialog, AgentTaskDefinition>(parameters);
        if (result.IsOk && result.Data is not null)
        {
            await TryRunPageCommandAsync(LoadTasksAsync);
            await SelectTaskAsync(result.Data.Id);
            ToastService.Show(CommonLoc.GetOrDefault(CommonTextKeys.TaskUpdatedSuccessfully, "Task updated successfully."), ToastType.Success);
        }
    }

    private async Task ToggleEnabled(AgentTaskDefinition task, ChangeEventArgs e)
    {
        var previousValue = task.IsEnabled;
        task.IsEnabled = e.Value as bool? == true;
        if (!await TryRunPageCommandAsync(() => ManagementApiClient.SetTaskEnabledAsync(task.Id, task.IsEnabled)))
        {
            task.IsEnabled = previousValue;
            return;
        }

        // A failed refresh must not visually undo a scheduler state the server already accepted.
        await TryRunPageCommandAsync(LoadTasksAsync);
    }

    private async Task DeleteTask(AgentTaskDefinition task)
    {
        var confirmResult = await DialogService.ShowAsync<ConfirmDialog>(
            CommonLoc.GetOrDefault(CommonTextKeys.TaskDeleteConfirmTitle, "Delete Task"),
            FormatCommonText(CommonTextKeys.TaskDeleteConfirmMessage, "Are you sure you want to delete \"{0}\"?", task.Name));

        if (confirmResult == DialogResult.Ok)
        {
            if (!await TryRunPageCommandAsync(() => ManagementApiClient.DeleteTaskAsync(task.Id)))
            {
                return;
            }

            await TryRunPageCommandAsync(LoadTasksAsync);
            ToastService.Show(CommonLoc.GetOrDefault(CommonTextKeys.TaskDeletedSuccessfully, "Task deleted."), ToastType.Success);
        }
    }

    private async Task ViewRuns(AgentTaskDefinition task)
    {
        var parameters = new Dictionary<string, object?>
        {
            { "Task", task },
            { "UserTimeZoneId", _userTimeZoneId }
        };

        await DialogService.ShowAsync<AgentTaskRunsDialog>(parameters);
    }

    private async Task RunTaskAsync(AgentTaskDefinition task)
    {
        var result = new TriggerTaskResult();
        if (!await TryRunPageCommandAsync(async () => result = await ManagementApiClient.TriggerTaskAsync(task.Id)))
        {
            return;
        }

        if (!result.Started)
        {
            ToastService.Show(result.Message, ToastType.Warning);
            return;
        }

        await TryRunPageCommandAsync(LoadTasksAsync);
        await SelectTaskAsync(task.Id);
        ToastService.Show(result.Message, ToastType.Success);
    }

    private async Task<bool> TryRunPageCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // A deleted account's circuit still has its original authentication state.
            NavigationManager.NavigateTo("/login", forceLoad: true);
            return false;
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, ToastType.Error);
            return false;
        }
    }

    private async Task RefreshAsync()
    {
        await TryRunPageCommandAsync(LoadTasksAsync);
    }

    private async Task SelectTaskAsync(string taskId)
    {
        _selectedTaskId = taskId;
        await Task.CompletedTask;
    }

    private async Task EnsureSelectionAsync()
    {
        // Reloads must not replay a notification that already opened its task.
        if (_shouldOpenRequestedTask && !string.IsNullOrWhiteSpace(_requestedTaskId) && _tasks.Any(task => string.Equals(task.Id, _requestedTaskId, StringComparison.Ordinal)))
        {
            _selectedTaskId = _requestedTaskId;
            await Task.CompletedTask;
            return;
        }

        if (_tasks.Count == 0)
        {
            _selectedTaskId = null;
            return;
        }

        var filteredTasks = FilteredTasks;
        if (filteredTasks.Count == 0)
        {
            return;
        }

        var hasVisibleSelection = filteredTasks.Any(task => string.Equals(task.Id, _selectedTaskId, StringComparison.Ordinal));
        if (!hasVisibleSelection)
        {
            _selectedTaskId = filteredTasks[0].Id;
        }

        await Task.CompletedTask;
    }

    private void ApplyNavigationStateFromQuery()
    {
        _requestedTaskId = null;
        _requestedRunId = null;
        _shouldOpenRequestedTask = false;
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.TryGetValue("taskId", out var taskId) && !string.IsNullOrWhiteSpace(taskId))
        {
            _requestedTaskId = taskId.ToString();
            _shouldOpenRequestedTask = true;
        }

        if (query.TryGetValue("runId", out var runId) && !string.IsNullOrWhiteSpace(runId))
        {
            _requestedRunId = runId.ToString();
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        _ = InvokeAsync(async () =>
        {
            ApplyNavigationStateFromQuery();
            await EnsureSelectionAsync();
            StateHasChanged();
        });
    }

    private async Task OpenTaskDetailsAsync(AgentTaskDefinition task)
    {
        await SelectTaskAsync(task.Id);

        var parameters = new Dictionary<string, object?>
        {
            ["Task"] = task,
            ["LastRun"] = GetLastRun(task.Id),
            ["FocusedRunId"] = string.Equals(task.Id, _requestedTaskId, StringComparison.Ordinal) ? _requestedRunId : null,
            ["UserTimeZoneId"] = _userTimeZoneId,
            ["AgentName"] = GetAgentName(task.AgentId),
            ["CreatorAgentName"] = string.IsNullOrWhiteSpace(task.CreatedByAgentId)
                ? null
                : GetAgentName(task.CreatedByAgentId),
            ["ModelLabel"] = GetModelLabel(task),
            ["ScheduleText"] = FormatSchedule(task),
            ["ScheduleSupportText"] = GetScheduleSupportText(task),
            ["StatusText"] = GetTaskStatusText(task),
            ["StatusBadgeClass"] = GetTaskStatusBadgeClass(task),
            ["IsRunning"] = IsTaskRunning(task.Id),
            ["IsOrphan"] = IsTaskOrphan(task)
        };

        var result = await DialogService.ShowAsync<AgentTaskDetailsDialog, string>(parameters);
        if (!result.IsOk || string.IsNullOrWhiteSpace(result.Data))
        {
            return;
        }

        if (result.Data == AgentTaskDetailsDialog.RunAction)
        {
            await RunTaskAsync(task);
        }
        else if (result.Data == AgentTaskDetailsDialog.RunsAction)
        {
            await ViewRuns(task);
        }
        else if (result.Data == AgentTaskDetailsDialog.EditAction)
        {
            await OpenEditDialog(task);
        }
    }

    private string GetAgentName(string agentId)
    {
        return _availableAgents.FirstOrDefault(agent => agent.Id == agentId)?.Name ?? agentId;
    }

    private List<SelectableTask> BuildSelectableTasks() =>
        _tasks.ConvertAll(task => new SelectableTask { Id = task.Id, Name = task.Name });

    private AgentTaskRun? GetLastRun(string taskId)
    {
        return _lastRuns.GetValueOrDefault(taskId);
    }

    // Skipped runs raise the same "needs attention" notice as failures, so both count here.
    private bool NeedsAttention(string taskId) =>
        GetLastRun(taskId)?.Status is AgentTaskStatus.Failed or AgentTaskStatus.Skipped;

    private bool IsTaskRunning(string taskId)
    {
        return _runningTaskIds.Contains(taskId);
    }

    private bool IsTaskOrphan(AgentTaskDefinition task)
    {
        return string.IsNullOrWhiteSpace(task.AgentId) || !_availableAgentIds.Contains(task.AgentId);
    }

    private bool MatchesFilter(AgentTaskDefinition task)
    {
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var searchText = _searchText.Trim();
            if (!task.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) &&
                !GetInstructionPreview(task).Contains(searchText, StringComparison.OrdinalIgnoreCase) &&
                !GetAgentName(task.AgentId).Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return _statusFilter switch
        {
            TaskStatusFilter.All => true,
            TaskStatusFilter.Disabled => !task.IsEnabled,
            TaskStatusFilter.NeverRun => GetLastRun(task.Id) is null,
            TaskStatusFilter.Running => GetLastRun(task.Id)?.Status == AgentTaskStatus.Running,
            TaskStatusFilter.Completed => GetLastRun(task.Id)?.Status == AgentTaskStatus.Completed,
            TaskStatusFilter.NeedsAttention => NeedsAttention(task.Id),
            TaskStatusFilter.Orphan => IsTaskOrphan(task),
            _ => true
        };
    }

    private string GetInstructionPreview(AgentTaskDefinition task) => task.InstructionSource is null ? task.Instruction
        : _instructions.GetValueOrDefault(task.Id) ?? CommonLoc.GetOrDefault(CommonTextKeys.TaskInstructionDefaultUnavailable,
            "The task's default instruction is unavailable. Enable its plugin and restart.");

    private string GetModelLabel(AgentTaskDefinition task)
    {
        if (string.IsNullOrWhiteSpace(task.ModelOverrideId))
        {
            return CommonLoc.GetOrDefault(CommonTextKeys.TaskDefaultModelProvider, "Use agent default");
        }

        var provider = _availableModelProviders.FirstOrDefault(config =>
            string.Equals(config.Id, task.ModelOverrideId, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            return CommonLoc.GetOrDefault(CommonTextKeys.TaskCustomProvider, "Custom provider");
        }

        return string.IsNullOrWhiteSpace(provider.ModelId)
            ? provider.Name
            : $"{provider.Name} ({provider.ModelId})";
    }

    private string GetScheduleSupportText(AgentTaskDefinition task)
    {
        if (task.Schedule.Kind == TaskScheduleKind.OneShot)
        {
            return task.Schedule.DeleteAfterRun
                ? CommonLoc.GetOrDefault(CommonTextKeys.TaskDeletesAfterSuccessfulRun, "Deletes itself after a successful run")
                : CommonLoc.GetOrDefault(CommonTextKeys.TaskDisablesAfterSuccessfulRun, "Disables itself after a successful run");
        }

        if (task.Schedule.Kind == TaskScheduleKind.Cron)
        {
            if (GetNextCronRun(task) is not null)
            {
                return FormatCommonText(CommonTextKeys.TaskCronExpression, "Cron: {0}", task.Schedule.CronExpression!);
            }

            var zone = string.IsNullOrWhiteSpace(task.Schedule.TimeZoneId)
                ? CommonLoc.GetOrDefault(CommonTextKeys.TaskServerLocalTime, "server-local time")
                : TimeZoneResolver.ResolveOrLocal(task.Schedule.TimeZoneId).DisplayName;

            return FormatCommonText(CommonTextKeys.TaskEvaluatedInTimeZone, "Evaluated in {0}", zone);
        }

        if (task.Schedule.Kind == TaskScheduleKind.AfterTask)
        {
            return CommonLoc.GetOrDefault(CommonTextKeys.TaskAfterTaskSupport, "Runs after the selected task succeeds");
        }

        return task.IsEnabled
            ? CommonLoc.GetOrDefault(CommonTextKeys.TaskWatchingForNextWindow, "Watching for the next scheduled window")
            : CommonLoc.GetOrDefault(CommonTextKeys.TaskPausedUntilEnabled, "Task is paused until enabled again");
    }

    private string GetTaskStatusText(AgentTaskDefinition task)
        => AgentTaskStatusDisplay.Get(task, GetLastRun(task.Id)?.Status, IsTaskOrphan(task), CommonLoc).Text;

    private string GetTaskStatusBadgeClass(AgentTaskDefinition task)
        => AgentTaskStatusDisplay.Get(task, GetLastRun(task.Id)?.Status, IsTaskOrphan(task), CommonLoc).BadgeClass;

    private string FormatSchedule(AgentTaskDefinition task)
    {
        var schedule = task.Schedule;
        var nextCronRun = GetNextCronRun(task);
        if (nextCronRun is not null)
        {
            return FormatCommonText(CommonTextKeys.TaskNextRun, "Next run {0}", FormatTimestamp(nextCronRun.Value));
        }

        return schedule.Kind switch
        {
            TaskScheduleKind.Interval when schedule.IntervalMinutes.HasValue =>
                schedule.IntervalMinutes.Value >= 60
                    ? FormatCommonText(CommonTextKeys.TaskEveryHoursMinutes, "Every {0}h {1}m", schedule.IntervalMinutes.Value / 60, schedule.IntervalMinutes.Value % 60)
                    : FormatCommonText(CommonTextKeys.TaskEveryMinutes, "Every {0}m", schedule.IntervalMinutes.Value),
            TaskScheduleKind.OneShot when schedule.RunAt.HasValue =>
                FormatCommonText(CommonTextKeys.TaskOnceAt, "Once at {0}", schedule.RunAt.Value.ToUserTime(_userTimeZone).ToString("g")),
            TaskScheduleKind.Cron when !string.IsNullOrWhiteSpace(schedule.CronExpression) =>
                FormatCommonText(CommonTextKeys.TaskCronExpression, "Cron: {0}", schedule.CronExpression),
            TaskScheduleKind.AfterTask when !string.IsNullOrWhiteSpace(schedule.PredecessorTaskId) =>
                FormatCommonText(CommonTextKeys.TaskAfterTaskSummary, "After: {0}", ResolvePredecessorName(schedule.PredecessorTaskId)),
            _ => CommonLoc.GetOrDefault(CommonTextKeys.NotConfigured, "Not configured")
        };
    }

    // Enabled cron tasks show their next slot because raw cron syntax means little to most users.
    // An overdue slot runs on the scheduler's next check, so it keeps the cron text instead of a later time.
    private DateTimeOffset? GetNextCronRun(AgentTaskDefinition task)
    {
        if (!task.IsEnabled || task.Schedule.Kind != TaskScheduleKind.Cron || string.IsNullOrWhiteSpace(task.Schedule.CronExpression)
            || TaskScheduleEvaluator.IsDue(task, GetLastRun(task.Id), DateTimeOffset.UtcNow))
        {
            return null;
        }

        try
        {
            return CronExpression.Parse(task.Schedule.CronExpression)
                .GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneResolver.ResolveOrLocal(task.Schedule.TimeZoneId));
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    private string ResolvePredecessorName(string predecessorTaskId) =>
        _tasks.FirstOrDefault(task => string.Equals(task.Id, predecessorTaskId, StringComparison.Ordinal))?.Name
        ?? CommonLoc.GetOrDefault(CommonTextKeys.TaskPredecessorMissing, "Deleted task");

    private string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToUserTime(_userTimeZone).ToString("g");
    }

    private string FormatRunTimestamp(AgentTaskRun run)
    {
        if (run.Status == AgentTaskStatus.Running)
        {
            return run.StartedAt is null
                ? CommonLoc.GetOrDefault(CommonTextKeys.TaskRunningNow, "Running now")
                : FormatCommonText(CommonTextKeys.TaskStartedAt, "Started {0}", FormatTimestamp(run.StartedAt.Value));
        }

        if (run.CompletedAt is not null)
        {
            return FormatTimestamp(run.CompletedAt.Value);
        }

        if (run.StartedAt is not null)
        {
            return FormatTimestamp(run.StartedAt.Value);
        }

        return CommonLoc.GetOrDefault(CommonTextKeys.Unknown, "Unknown");
    }

    private string FormatCommonText(string key, string fallback, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, CommonLoc.GetOrDefault(key, fallback), args);
    }

    private static string GetStatusBadgeClass(AgentTaskStatus status)
    {
        return status switch
        {
            AgentTaskStatus.Completed => "badge-success",
            AgentTaskStatus.Running => "badge-warning",
            AgentTaskStatus.Failed => "badge-error",
            AgentTaskStatus.Pending => "badge-neutral",
            AgentTaskStatus.Skipped => "badge-warning",
            _ => "badge-ghost"
        };
    }

    private string GetRunStatusText(AgentTaskStatus status)
    {
        return AgentTaskStatusDisplay.GetRunText(status, CommonLoc);
    }

    private enum TaskStatusFilter
    {
        All,
        Running,
        Completed,
        NeedsAttention,
        Orphan,
        NeverRun,
        Disabled
    }

    private async Task HandleTaskStatusChangedAsync()
    {
        await LoadTasksAsync();
        StateHasChanged();
    }

    private async Task OnTaskStatusUpdatedAsync(TaskStatusUpdate _)
    {
        await InvokeAsync(HandleTaskStatusChangedAsync);
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
        if (SystemHubClient.OnTaskStatusUpdatedAsync == OnTaskStatusUpdatedAsync)
        {
            SystemHubClient.OnTaskStatusUpdatedAsync = null;
        }
    }
}

