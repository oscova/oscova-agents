using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Persistence;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Tests.Framework.Tasks;

public sealed class AgentTaskCatalogTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"oscova-task-catalog-{Guid.NewGuid():N}");
    private readonly YamlConverter _yamlConverter = new();

    [Fact]
    public async Task ReloadAsync_ReevaluatesRootAndAppliesCallerPolicies()
    {
        var firstRoot = Path.Combine(_temporaryRoot, "first-users");
        var secondRoot = Path.Combine(_temporaryRoot, "second-users");
        const string firstTaskId = "first-task";
        const string rejectedTaskId = "rejected-task";
        const string secondTaskId = "second-task";

        await WriteTaskAsync(firstRoot, "accepted-first", "RESERVED", firstTaskId);
        await WriteTaskAsync(firstRoot, "rejected-first", "ordinary", rejectedTaskId);
        await WriteTaskAsync(secondRoot, "accepted-second", "ordinary", secondTaskId);

        var currentRoot = firstRoot;
        var resolverCalls = 0;
        var catalog = new AgentTaskCatalog(
            () =>
            {
                resolverCalls++;
                return currentRoot;
            },
            _yamlConverter,
            NullLogger<AgentTaskCatalog>.Instance,
            new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector(),
            static userId => userId.StartsWith("accepted-", StringComparison.Ordinal));

        await catalog.ReloadAsync();

        Assert.Equal("accepted-first", catalog.TryGetOwnerUserId(firstTaskId));
        Assert.Null(catalog.TryGetTaskDirectory(rejectedTaskId));
        Assert.Single(catalog.GetAllEnabledTasks());

        currentRoot = secondRoot;
        await catalog.ReloadAsync();

        Assert.Equal(2, resolverCalls);
        Assert.Null(catalog.TryGetTaskDirectory(firstTaskId));
        Assert.Equal("accepted-second", catalog.TryGetOwnerUserId(secondTaskId));
    }

    [Fact]
    public async Task ReloadUserAsync_ReplacesOnlyRequestedUserSlice()
    {
        var usersRoot = Path.Combine(_temporaryRoot, "users");
        const string firstUserId = "first-user";
        const string secondUserId = "second-user";
        const string oldTaskId = "old-task";
        const string replacementTaskId = "replacement-task";
        const string otherTaskId = "other-task";

        var oldTaskDirectory = await WriteTaskAsync(usersRoot, firstUserId, "old", oldTaskId);
        await WriteTaskAsync(usersRoot, secondUserId, "other", otherTaskId);

        var catalog = CreateCatalog(usersRoot);
        await catalog.ReloadAsync();

        Directory.Delete(oldTaskDirectory, recursive: true);
        await WriteTaskAsync(usersRoot, firstUserId, "replacement", replacementTaskId);
        await catalog.ReloadUserAsync(firstUserId);

        Assert.Null(catalog.TryGetTaskDirectory(oldTaskId));
        Assert.Equal(firstUserId, catalog.TryGetOwnerUserId(replacementTaskId));
        Assert.Equal(secondUserId, catalog.TryGetOwnerUserId(otherTaskId));
    }

    [Fact]
    public async Task ReloadUserAsync_WhenUserIdIsUnsafe_DoesNotReadOutsideRoot()
    {
        var usersRoot = Path.Combine(_temporaryRoot, "users");
        const string safeTaskId = "safe-task";
        const string outsideTaskId = "outside-task";

        await WriteTaskAsync(usersRoot, "safe-user", "safe", safeTaskId);
        var outsideTaskDirectory = await WriteTaskAsync(
            _temporaryRoot,
            "outside-user",
            "outside",
            outsideTaskId);

        var catalog = CreateCatalog(usersRoot);
        await catalog.ReloadAsync();
        await catalog.ReloadUserAsync(Path.Combine("..", "outside-user"));

        Assert.Equal("safe-user", catalog.TryGetOwnerUserId(safeTaskId));
        Assert.Null(catalog.TryGetTaskDirectory(outsideTaskId));
        Assert.True(File.Exists(Path.Combine(outsideTaskDirectory, "task.md.enc")));
    }

    [Fact]
    public async Task ReloadAsync_WhenTaskFileIsMalformed_SkipsItAndRetainsValidTask()
    {
        var usersRoot = Path.Combine(_temporaryRoot, "users");
        const string userId = "user";
        const string validTaskId = "valid-task";

        await WriteTaskAsync(usersRoot, userId, "valid", validTaskId);
        var malformedTaskDirectory = Path.Combine(usersRoot, userId, "tasks", "malformed");
        Directory.CreateDirectory(malformedTaskDirectory);
        await Secrets.EncryptedFileContent.WriteAllTextAsync(userId, Path.Combine(malformedTaskDirectory, "task.md.enc"), "---\nid: [unterminated\n---\nRun the task.", new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector());

        var catalog = CreateCatalog(usersRoot);

        await catalog.ReloadAsync();

        var loaded = Assert.Single(catalog.GetTasksForUser(userId));
        Assert.Equal(validTaskId, loaded.TaskId);
    }

    [Fact]
    public async Task Reloads_WhenPersistedDefinitionIsInvalid_ReportItOncePerLoad()
    {
        var usersRoot = Path.Combine(_temporaryRoot, "users");
        const string userId = "user";
        const string taskId = "invalid-task";
        await WriteTaskAsync(usersRoot, userId, "invalid", taskId);
        var validationCalls = 0;
        var logger = new CapturingLogger<AgentTaskCatalog>();
        var catalog = new AgentTaskCatalog(
            () => usersRoot,
            _yamlConverter,
            logger,
            new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector(),
            persistedDefinitionValidator: file =>
            {
                validationCalls++;
                return file.Id == taskId ? "Invalid schedule." : null;
            });

        await catalog.ReloadAsync();
        await catalog.ReloadUserAsync(userId);

        Assert.Equal(2, validationCalls);
        Assert.Equal(2, logger.Messages.Count(message =>
            message.Contains(taskId, StringComparison.Ordinal) &&
            message.Contains("Invalid schedule.", StringComparison.Ordinal)));
        Assert.NotNull(catalog.TryGetTaskDirectory(taskId));
    }

    [Fact]
    public async Task ReadResults_WhenCallerAttemptsMutation_PreservePublishedState()
    {
        var usersRoot = Path.Combine(_temporaryRoot, "users");
        const string userId = "user";
        await WriteTaskAsync(usersRoot, userId, "task", "task-id");
        var catalog = CreateCatalog(usersRoot);
        await catalog.ReloadAsync();

        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<ICollection<AgentTaskCatalogEntry>>(catalog.GetTasksForUser(userId)).Clear());
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<ICollection<AgentTaskCatalogEntry>>(catalog.GetAllEnabledTasks()).Clear());

        Assert.Single(catalog.GetTasksForUser(userId));
        Assert.Single(catalog.GetAllEnabledTasks());
    }

    [SkippableFact]
    public async Task ReloadAsync_WhenUserOrTaskDirectoryIsLinkedOutsideRoot_SkipsLinks()
    {
        var usersRoot = Path.Combine(_temporaryRoot, "users");
        var linkedUserSource = Path.Combine(_temporaryRoot, "linked-user-source");
        var linkedUserDirectory = Path.Combine(usersRoot, "linked-user");
        var linkedTaskSource = await WriteTaskAsync(
            _temporaryRoot,
            "linked-task-source",
            "outside-task",
            "linked-task-id");
        var linkedTaskDirectory = Path.Combine(usersRoot, "safe-user", "tasks", "linked-task");

        await WriteTaskAsync(_temporaryRoot, "linked-user-source", "outside-task", "linked-user-task-id");
        await WriteTaskAsync(usersRoot, "linked-user", "inside-task", "inside-task-id");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedTaskDirectory)!);
        var catalog = CreateCatalog(usersRoot);
        await catalog.ReloadAsync();
        Assert.NotNull(catalog.TryGetTaskDirectory("inside-task-id"));
        Directory.Delete(linkedUserDirectory, recursive: true);

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkedUserDirectory, linkedUserSource);
                Directory.CreateSymbolicLink(linkedTaskDirectory, linkedTaskSource);
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            {
                Skip.If(true, $"Symbolic link creation is unavailable on this runner: {exception.Message}");
            }

            await catalog.ReloadUserAsync("linked-user");

            Assert.Null(catalog.TryGetTaskDirectory("inside-task-id"));
            Assert.Null(catalog.TryGetTaskDirectory("linked-user-task-id"));

            await catalog.ReloadAsync();

            Assert.Equal(
                (LinkedUserTask: (string?)null, LinkedTask: (string?)null),
                (
                    LinkedUserTask: catalog.TryGetTaskDirectory("linked-user-task-id"),
                    LinkedTask: catalog.TryGetTaskDirectory("linked-task-id")));
        }
        finally
        {
            if (Directory.Exists(linkedTaskDirectory))
            {
                Directory.Delete(linkedTaskDirectory);
            }

            if (Directory.Exists(linkedUserDirectory))
            {
                Directory.Delete(linkedUserDirectory);
            }
        }
    }

    [Fact]
    public void Constructor_WhenUsersRootResolverIsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentTaskCatalog(
            null!,
            _yamlConverter,
            NullLogger<AgentTaskCatalog>.Instance,
            new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }

    private AgentTaskCatalog CreateCatalog(string usersRoot)
    {
        return new AgentTaskCatalog(
            () => usersRoot,
            _yamlConverter,
            NullLogger<AgentTaskCatalog>.Instance,
            new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector());
    }

    private async Task<string> WriteTaskAsync(
        string usersRoot,
        string userId,
        string taskDirectoryName,
        string taskId)
    {
        var taskDirectory = Path.Combine(usersRoot, userId, "tasks", taskDirectoryName);
        Directory.CreateDirectory(taskDirectory);

        var taskFile = new AgentTaskDefinitionFile
        {
            Id = taskId,
            Name = taskId,
            AgentId = "agent-id",
            Schedule = new ScheduleFile
            {
                Kind = "interval",
                IntervalMinutes = 30
            },
            IsEnabled = true,
            NotifyOnCompletion = false,
            SessionMode = "isolated",
            CreatedAt = "2026-08-01T00:00:00Z"
        };
        var yaml = _yamlConverter.Serialize(taskFile);
        var content = MarkdownFrontmatter.Combine(yaml, "Run the task.");
        await Secrets.EncryptedFileContent.WriteAllTextAsync(userId, Path.Combine(taskDirectory, "task.md.enc"), content, new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector());
        return taskDirectory;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
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
            Assert.Equal(LogLevel.Warning, logLevel);
            Messages.Add(formatter(state, exception));
        }
    }
}