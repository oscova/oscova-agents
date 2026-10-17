using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Persistence;
using Oscova.Agents.Policies;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Tests.Framework.Tasks;

public sealed class AgentTaskRepositoryExternalTriggerTests : IDisposable
{
    private readonly string _userId = Guid.NewGuid().ToString();
    private readonly string _usersRoot = Path.Combine(
        Path.GetTempPath(),
        $"oscova-task-repository-trigger-tests-{Guid.NewGuid():N}");
    private readonly YamlConverter _yamlConverter = new();

    [Fact]
    public async Task SaveAndGetTaskAsync_RoundTripsExternalTriggerYaml()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Nightly Build");
        task.ExternalTrigger = new TaskExternalTrigger
        {
            IsEnabled = true,
            Parameters = [new TaskExternalParameter { Name = "branch", Kind = TaskExternalParameterKind.String, IsRequired = true }]
        };

        await repository.SaveTaskAsync(task);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var taskFile = (await Secrets.EncryptedFileContent.ReadAllTextAsync(_userId, Path.Combine(taskDirectory!, "task.md.enc"), new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector()))!;
        Assert.Contains("external_trigger:", taskFile, StringComparison.Ordinal);
        Assert.Contains("slug: " + _userId + "-nightly-build", taskFile, StringComparison.Ordinal);
        Assert.Contains("name: branch", taskFile, StringComparison.Ordinal);

        var loaded = await repository.GetTaskByExternalTriggerSlugAsync(_userId + "-nightly-build");
        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded!.Id);
        Assert.True(loaded.ExternalTrigger.IsEnabled);
        Assert.Equal(_userId + "-nightly-build", loaded.ExternalTrigger.Slug);
        Assert.Equal("branch", Assert.Single(loaded.ExternalTrigger.Parameters).Name);
    }

    [Fact]
    public async Task SaveTaskAsync_WhenExternalTriggerSlugIsDuplicated_Throws()
    {
        var (repository, _) = CreateRepository();
        var first = CreateTask("First Build");
        first.ExternalTrigger = new TaskExternalTrigger { IsEnabled = true, Slug = "shared-build" };
        var second = CreateTask("Second Build");
        second.ExternalTrigger = new TaskExternalTrigger { IsEnabled = true, Slug = "shared-build" };

        await repository.SaveTaskAsync(first);

        var exception = await Assert.ThrowsAsync<DuplicateExternalTriggerSlugException>(() => repository.SaveTaskAsync(second));
        Assert.Equal($"External trigger slug '{_userId}-shared-build' is already used by another task.", exception.Message);
    }

    [Fact]
    public async Task SaveTaskAsync_WhenExternalTriggerSlugNeedsCleanup_NormalizesIt()
    {
        var (repository, _) = CreateRepository();
        var task = CreateTask("Nightly Build");
        task.ExternalTrigger = new TaskExternalTrigger { IsEnabled = true, Slug = " Nightly Build " };

        await repository.SaveTaskAsync(task);

        var loaded = await repository.GetTaskAsync(_userId, task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(_userId + "-nightly-build", loaded!.ExternalTrigger.Slug);
    }

    [Fact]
    public async Task GetTaskByExternalTriggerSlugAsync_WhenOneShotTaskIsDisabled_StillFindsTask()
    {
        var (repository, _) = CreateRepository();
        var task = CreateTask("Nightly Build");
        task.Schedule = new TaskSchedule { Kind = TaskScheduleKind.OneShot, RunAt = DateTimeOffset.UtcNow };
        task.ExternalTrigger = new TaskExternalTrigger { IsEnabled = true, Slug = "nightly-build" };
        await repository.SaveTaskAsync(task);

        task.IsEnabled = false;
        await repository.SaveTaskAsync(task);

        var loaded = await repository.GetTaskByExternalTriggerSlugAsync(_userId + "-nightly-build");

        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded!.Id);
        Assert.False(loaded.IsEnabled);
    }

    [Fact]
    public async Task SaveTaskAsync_WhenExternalTriggerIsDisabled_PreservesSlugAndParameters()
    {
        var (repository, _) = CreateRepository();
        var task = CreateTask("Nightly Build");
        task.ExternalTrigger = new TaskExternalTrigger
        {
            IsEnabled = false,
            Slug = " Nightly Build ",
            Parameters = [new TaskExternalParameter { Name = " branch ", Kind = TaskExternalParameterKind.String, IsRequired = true }]
        };

        await repository.SaveTaskAsync(task);

        var loaded = await repository.GetTaskAsync(_userId, task.Id);

        Assert.NotNull(loaded);
        Assert.False(loaded!.ExternalTrigger.IsEnabled);
        Assert.Equal(_userId + "-nightly-build", loaded.ExternalTrigger.Slug);
        var parameter = Assert.Single(loaded.ExternalTrigger.Parameters);
        Assert.Equal("branch", parameter.Name);
        Assert.Equal(TaskExternalParameterKind.String, parameter.Kind);
        Assert.True(parameter.IsRequired);
    }

    [Fact]
    public async Task SaveAndGetRunAsync_RoundTripsTriggerMetadata()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Nightly Build");
        await repository.SaveTaskAsync(task);
        using var document = JsonDocument.Parse("{\"branch\":\"main\"}");
        var run = new AgentTaskRun
        {
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            TriggerSource = TaskTriggerSource.ExternalWebhook,
            TriggerParameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["branch"] = document.RootElement.GetProperty("branch").Clone()
            }
        };

        await repository.SaveRunAsync(run);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var runsDirectory = Path.Combine(taskDirectory!, "runs");
        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(runsDirectory, "index.jsonl")));
        line = new PassThroughContentProtector().Unprotect(_userId, $"task\0{task.Id}\0index", line)!;
        Assert.Contains("\"trigger_source\":\"external_webhook\"", line, StringComparison.Ordinal);
        Assert.Contains("\"trigger_parameters\"", line, StringComparison.Ordinal);

        var loaded = await repository.GetRunAsync(_userId, task.Id, run.Id);
        Assert.NotNull(loaded);
        Assert.Equal(TaskTriggerSource.ExternalWebhook, loaded!.TriggerSource);
        Assert.Equal("main", loaded.TriggerParameters["branch"].GetString());
    }

    [Fact]
    public async Task GetRunAsync_WhenRunHistoryHasMalformedLines_LogsActualLineNumbers()
    {
        var logger = new CapturingLogger();
        var (repository, catalog) = CreateRepository(logger);
        var task = CreateTask("Nightly Build");
        await repository.SaveTaskAsync(task);
        var run = new AgentTaskRun
        {
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        await repository.SaveRunAsync(run);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var runsDirectory = Path.Combine(taskDirectory!, "runs");
        await File.AppendAllTextAsync(Path.Combine(runsDirectory, "index.jsonl"), new PassThroughContentProtector().Protect(_userId, $"task\0{task.Id}\0index", "not-json") + Environment.NewLine);

        var loaded = await repository.GetRunAsync(_userId, task.Id, run.Id);

        Assert.NotNull(loaded);
        Assert.Equal(run.Id, loaded!.Id);
        Assert.Contains(logger.Messages, message => message.Contains("line 2", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_usersRoot))
        {
            Directory.Delete(_usersRoot, recursive: true);
        }
    }

    private (AgentTaskRepository Repository, AgentTaskCatalog Catalog) CreateRepository(ILogger<AgentTaskRepository>? logger = null)
    {
        var catalog = new AgentTaskCatalog(
            () => _usersRoot,
            _yamlConverter,
            NullLogger<AgentTaskCatalog>.Instance,
            new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector(),
            static userId => Guid.TryParse(userId, out _));
        var repository = new AgentTaskRepository(
            () => _usersRoot,
            catalog,
            _yamlConverter,
            logger ?? NullLogger<AgentTaskRepository>.Instance,
            new FileSessionIndexCoordinator(),
            new PassThroughContentProtector());
        return (repository, catalog);
    }

    private AgentTaskDefinition CreateTask(string name)
    {
        return new AgentTaskDefinition
        {
            Id = Guid.NewGuid().ToString(),
            UserId = _userId,
            AgentId = Guid.NewGuid().ToString(),
            Name = name,
            Instruction = "Summarize the queue.",
            Schedule = new TaskSchedule
            {
                Kind = TaskScheduleKind.Interval,
                IntervalMinutes = 30
            },
            IsEnabled = true,
            NotifyOnCompletion = false,
            SessionMode = TaskSessionMode.Isolated,
            ToolApprovalRunMode = ToolApprovalRunMode.AutoRejectRequired,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class CapturingLogger : ILogger<AgentTaskRepository>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}