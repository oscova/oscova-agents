using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;
using Oscova.Agents.Persistence;
using Oscova.Agents.Policies;
using Oscova.Agents.Secrets;
using Oscova.Agents.Tasks;
using Oscova.Agents.Workspace;
using System.Text.Json;

namespace Oscova.Agents.Tests.Framework.Tasks;

public sealed class AgentTaskRepositoryTests : IDisposable
{
    private readonly string _userId = Guid.NewGuid().ToString();
    private readonly string _usersRoot = Path.Combine(
        Path.GetTempPath(),
        $"oscova-task-repository-tests-{Guid.NewGuid():N}");
    private readonly YamlConverter _yamlConverter = new();

    /// <summary>A task deleted after catalog enumeration cannot hide the remaining task list.</summary>
    [Fact]
    public async Task GetTasksByUser_AfterConcurrentDeletion_ReturnsRemainingDefinitions()
    {
        var (repository, catalog) = CreateRepository();
        var deleted = CreateTask("Deleted task");
        var retained = CreateTask("Retained task");
        await repository.SaveTaskAsync(deleted);
        await repository.SaveTaskAsync(retained);
        var snapshotCatalog = new CancelOnReloadAgentTaskCatalog(catalog)
        {
            TasksEnumerated = () => repository.DeleteTaskAsync(_userId, deleted.Id).GetAwaiter().GetResult()
        };
        var reader = CreateRepository(snapshotCatalog);
        var remaining = await reader.GetTasksByUserAsync(_userId);
        Assert.Equal(retained.Id, Assert.Single(remaining).Id);
        Assert.Null(await repository.GetTaskAsync(_userId, deleted.Id));
    }

    [Fact]
    public async Task EnsureTask_PreservesEditedDefinitionAndHistory()
    {
        var (repository, catalog) = CreateRepository();
        var initial = CreateTask("Seeded task");
        initial.RequiresHandler = true;
        await repository.EnsureTaskAsync("plugin-example.record-run", initial);
        var directory = Assert.IsType<string>(catalog.TryGetTaskDirectory(initial.Id));
        var file = Path.Combine(directory, "task.md.enc");
        var saved = Assert.IsType<AgentTaskDefinition>(await repository.GetTaskAsync(_userId, initial.Id));
        saved.Name = "Edited task";
        saved.Schedule.IntervalMinutes = 45;
        await repository.SaveTaskAsync(saved);
        await repository.SaveRunAsync(new AgentTaskRun
        {
            Id = "first-run",
            TaskId = saved.Id,
            UserId = saved.UserId,
            Status = AgentTaskStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ResultSummary = "Recorded."
        });
        var bytes = await File.ReadAllBytesAsync(file);

        await repository.EnsureTaskAsync("plugin-example.record-run", CreateTask("New default"));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        var restored = Assert.IsType<AgentTaskDefinition>(await repository.GetTaskAsync(_userId, initial.Id));
        Assert.Equal(saved.Id, restored.Id);
        Assert.Equal(saved.CreatedAt.ToUnixTimeSeconds(), restored.CreatedAt.ToUnixTimeSeconds());
        Assert.Equal("Edited task", restored.Name);
        Assert.Equal(45, restored.Schedule.IntervalMinutes);
        Assert.True(restored.RequiresHandler);
        Assert.Equal("Recorded.", Assert.Single(await repository.GetRunsAsync(_userId, initial.Id)).ResultSummary);
    }

    [SkippableFact]
    public async Task EnsureTask_RejectsEscapingDirectory()
    {
        var (repository, _) = CreateRepository();
        var task = CreateTask("Safe task");
        await Assert.ThrowsAsync<ArgumentException>(() => repository.EnsureTaskAsync("../outside", task));
        Assert.False(Directory.Exists(Path.Combine(_usersRoot, _userId, "outside")));

        var tasksRoot = Path.Combine(_usersRoot, _userId, "tasks");
        var outside = Path.Combine(_usersRoot, "outside");
        Directory.CreateDirectory(tasksRoot);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(tasksRoot, "plugin-link");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            {
                Skip.If(true, $"Symbolic link creation is unavailable on this runner: {exception.Message}");
            }

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.EnsureTaskAsync("plugin-link", task));
            Assert.False(File.Exists(Path.Combine(outside, "task.md.enc")));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public void Constructor_WhenUsersRootResolverIsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentTaskRepository(
            null!,
            CreateCatalog(),
            _yamlConverter,
            NullLogger<AgentTaskRepository>.Instance,
            new FileSessionIndexCoordinator(),
            new PassThroughContentProtector()));
    }

    [Fact]
    public async Task SaveTaskAsync_WhenUsersRootChanges_UsesCurrentRoot()
    {
        var firstRoot = Path.Combine(_usersRoot, "first");
        var secondRoot = Path.Combine(_usersRoot, "second");
        var currentRoot = firstRoot;
        var catalog = new AgentTaskCatalog(
            () => currentRoot,
            _yamlConverter,
            NullLogger<AgentTaskCatalog>.Instance,
            new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector());
        var repository = new AgentTaskRepository(
            () => currentRoot,
            catalog,
            _yamlConverter,
            NullLogger<AgentTaskRepository>.Instance,
            new FileSessionIndexCoordinator(),
            new PassThroughContentProtector());
        var firstTask = CreateTask("First Root Task");
        await repository.SaveTaskAsync(firstTask);

        currentRoot = secondRoot;
        var secondTask = CreateTask("Second Root Task");
        await repository.SaveTaskAsync(secondTask);

        Assert.True(File.Exists(Path.Combine(firstRoot, _userId, "tasks", "first-root-task", "task.md.enc")));
        Assert.True(File.Exists(Path.Combine(secondRoot, _userId, "tasks", "second-root-task", "task.md.enc")));
        Assert.False(File.Exists(Path.Combine(firstRoot, _userId, "tasks", "second-root-task", "task.md.enc")));
    }

    [Fact]
    public async Task SaveTaskAsync_WhenCallerReservesSlug_WritesUniqueTaskFileWithoutClassificationFlag()
    {
        var (repository, catalog) = CreateRepository(reservedTaskSlugs: ["reserved-task"]);
        var task = CreateTask("Reserved Task");

        await repository.SaveTaskAsync(task);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        Assert.EndsWith($"tasks{Path.DirectorySeparatorChar}reserved-task-2", taskDirectory);

        var taskFile = (await EncryptedFileContent.ReadAllTextAsync(_userId, Path.Combine(taskDirectory!, "task.md.enc"), new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector()))!;
        Assert.Contains($"id: {task.Id}", taskFile);
        Assert.Contains("is_enabled: true", taskFile);
        Assert.DoesNotContain("is_system", taskFile, StringComparison.OrdinalIgnoreCase);

        var loaded = await repository.GetTaskAsync(_userId, task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(task.Instruction, loaded!.Instruction);
        Assert.Equal(_userId, loaded.UserId);
    }

    [Fact]
    public async Task SaveTaskAsync_WhenUserIdEscapesUsersRoot_RejectsWrite()
    {
        var (repository, _) = CreateRepository();
        var outsideUserDirectory = _usersRoot + "-outside-user";
        var task = CreateTask("Escaping User Task");
        task.UserId = Path.GetRelativePath(_usersRoot, outsideUserDirectory);

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTaskAsync(task));

            Assert.False(Directory.Exists(outsideUserDirectory));
        }
        finally
        {
            if (Directory.Exists(outsideUserDirectory))
            {
                Directory.Delete(outsideUserDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveTaskAsync_WhenTaskIdLooksLikeTraversal_UsesContainedSlugDirectory()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Contained Task Id");
        task.Id = Path.Combine("..", "outside-task-id");

        await repository.SaveTaskAsync(task);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        Assert.False(Path.GetRelativePath(_usersRoot, taskDirectory!).StartsWith("..", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveTaskAsync_WhenCancellationArrivesAfterWrite_RefreshesCatalog()
    {
        var innerCatalog = CreateCatalog();
        var catalog = new CancelOnReloadAgentTaskCatalog(innerCatalog);
        var repository = CreateRepository(catalog);
        var task = CreateTask("Cancellation Safe Save");
        using var cancellation = new CancellationTokenSource();
        catalog.CancelOnNextReload(cancellation);

        await repository.SaveTaskAsync(task, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.NotNull(catalog.TryGetTaskDirectory(task.Id));
        Assert.NotNull(await repository.GetTaskAsync(task.UserId, task.Id));
    }

    [Fact]
    public async Task DeleteTaskAsync_WhenCancellationArrivesAfterDelete_RefreshesCatalog()
    {
        var innerCatalog = CreateCatalog();
        var catalog = new CancelOnReloadAgentTaskCatalog(innerCatalog);
        var repository = CreateRepository(catalog);
        var task = CreateTask("Cancellation Safe Delete");
        await repository.SaveTaskAsync(task);
        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        using var cancellation = new CancellationTokenSource();
        catalog.CancelOnNextReload(cancellation);

        await repository.DeleteTaskAsync(task.UserId, task.Id, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(Directory.Exists(taskDirectory));
        Assert.Null(catalog.TryGetTaskDirectory(task.Id));
    }

    [Fact]
    public async Task SaveRunAsync_WhenRunSavedAgain_AppendsAndReadsBackLatestStateOnce()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Daily Inbox Triage");
        await repository.SaveTaskAsync(task);

        var run = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };

        await repository.SaveRunAsync(run);
        run.Status = AgentTaskStatus.Completed;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.ResultSummary = "Done.";
        await repository.SaveRunAsync(run);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var runsDirectory = Path.Combine(taskDirectory!, "runs");
        var indexLines = await File.ReadAllLinesAsync(Path.Combine(runsDirectory, "index.jsonl"));
        Assert.Single(indexLines);

        var latestRun = await repository.GetLatestRunAsync(_userId, task.Id);
        Assert.NotNull(latestRun);
        Assert.Equal(AgentTaskStatus.Completed, latestRun!.Status);
        Assert.Equal("Done.", latestRun.ResultSummary);

        var runs = await repository.GetRunsAsync(_userId, task.Id);
        Assert.Single(runs);
        Assert.Equal(AgentTaskStatus.Completed, runs[0].Status);
    }

    [Fact]
    public async Task SaveRunAsync_SplitsIndexTranscriptAndSessionFiles()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Daily Inbox Triage");
        await repository.SaveTaskAsync(task);
        var session = JsonDocument.Parse("{\"conversation\":\"abc\"}").RootElement.Clone();
        var run = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow,
            ResultSummary = "Done.",
            SystemInstruction = "Task instruction",
            SessionElement = session,
            LiveMessages =
            [
                new ChatMessage(ChatRole.Assistant, "Task response.")
            ]
        };

        await repository.SaveRunAsync(run);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var runsDirectory = Path.Combine(taskDirectory!, "runs");
        Assert.True(File.Exists(Path.Combine(runsDirectory, "index.jsonl")));
        Assert.True(File.Exists(Path.Combine(runsDirectory, run.Id + ".jsonl")));
        Assert.True(File.Exists(Path.Combine(runsDirectory, run.Id + ".session.json.enc")));
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(runsDirectory, run.Id + ".jsonl"))).Length);

        var loaded = await repository.GetRunAsync(_userId, task.Id, run.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Task instruction", loaded!.SystemInstruction);
        Assert.Equal("Task response.", loaded.LiveMessages!.Single().Text);
        Assert.True(loaded.SessionElement.HasValue);
        Assert.Equal("abc", loaded.SessionElement.Value.GetProperty("conversation").GetString());
    }

    /// <summary>Latest runs for task lists come from the run index without opening transcripts or sessions.</summary>
    [Fact]
    public async Task GetLatestRunsAsync_ReturnsIndexSummaryWithoutTranscriptOrSession()
    {
        // Arrange
        var (repository, _) = CreateRepository();
        var task = CreateTask("Daily Inbox Triage");
        await repository.SaveTaskAsync(task);
        await repository.SaveRunAsync(new AgentTaskRun
        {
            Id = "latest",
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow,
            ResultSummary = "Partly done.",
            Error = "Mailbox offline.",
            SessionElement = JsonDocument.Parse("{\"conversation\":\"abc\"}").RootElement.Clone(),
            LiveMessages = [new ChatMessage(ChatRole.Assistant, "Task response.")]
        });

        // Act
        var latest = (await repository.GetLatestRunsAsync([task.Id]))[task.Id];

        // Assert
        Assert.Equal("latest", latest.Id);
        Assert.Equal(AgentTaskStatus.Failed, latest.Status);
        Assert.Equal("Partly done.", latest.ResultSummary);
        Assert.Equal("Mailbox offline.", latest.Error);
        Assert.Null(latest.LiveMessages);
        Assert.Null(latest.SessionElement);
    }

    /// <summary>Keep last runs keeps the newest runs and deletes older index rows, transcripts and sessions.</summary>
    [Fact]
    public async Task SaveRunAsync_WhenKeepLastRunsIsSet_DeletesOlderRunsAndTheirFiles()
    {
        // Arrange
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Hourly sweep");
        task.KeepLastRuns = 2;
        await repository.SaveTaskAsync(task);
        var runsDirectory = Path.Combine(catalog.TryGetTaskDirectory(task.Id)!, "runs");
        var start = DateTimeOffset.UtcNow.AddHours(-4);

        // Act
        for (var hour = 0; hour < 4; hour++)
        {
            await repository.SaveRunAsync(CreateFinishedRun(task, $"run-{hour}", start.AddHours(hour)));
        }

        // Assert
        var kept = await repository.GetRunsAsync(_userId, task.Id);
        Assert.Equal(["run-3", "run-2"], kept.Select(run => run.Id));
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(runsDirectory, "index.jsonl"))).Length);
        foreach (var deleted in new[] { "run-0", "run-1" })
        {
            Assert.False(File.Exists(Path.Combine(runsDirectory, deleted + ".jsonl")));
            Assert.False(File.Exists(Path.Combine(runsDirectory, deleted + ".session.json.enc")));
        }

        Assert.True(File.Exists(Path.Combine(runsDirectory, "run-2.jsonl")));
        Assert.True(File.Exists(Path.Combine(runsDirectory, "run-2.session.json.enc")));
    }

    /// <summary>Deleting a run removes only that run's row and files, and only for its owner.</summary>
    [Fact]
    public async Task DeleteRunAsync_RemovesOwnedRunRowAndFiles()
    {
        // Arrange
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Daily Inbox Triage");
        await repository.SaveTaskAsync(task);
        var runsDirectory = Path.Combine(catalog.TryGetTaskDirectory(task.Id)!, "runs");
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        await repository.SaveRunAsync(CreateFinishedRun(task, "older", start));
        await repository.SaveRunAsync(CreateFinishedRun(task, "newer", start.AddHours(1)));

        // Act
        var deletedByOtherUser = await repository.DeleteRunAsync(Guid.NewGuid().ToString(), task.Id, "older");
        var deleted = await repository.DeleteRunAsync(_userId, task.Id, "older");
        var deletedAgain = await repository.DeleteRunAsync(_userId, task.Id, "older");

        // Assert
        Assert.False(deletedByOtherUser);
        Assert.True(deleted);
        Assert.False(deletedAgain);
        Assert.Equal("newer", Assert.Single(await repository.GetRunsAsync(_userId, task.Id)).Id);
        Assert.False(File.Exists(Path.Combine(runsDirectory, "older.jsonl")));
        Assert.False(File.Exists(Path.Combine(runsDirectory, "older.session.json.enc")));
        Assert.True(File.Exists(Path.Combine(runsDirectory, "newer.jsonl")));
    }

    private static AgentTaskRun CreateFinishedRun(AgentTaskDefinition task, string runId, DateTimeOffset startedAt)
    {
        return new AgentTaskRun
        {
            Id = runId,
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(1),
            ResultSummary = "Done.",
            SessionElement = JsonDocument.Parse("{\"conversation\":\"abc\"}").RootElement.Clone(),
            LiveMessages = [new ChatMessage(ChatRole.Assistant, "Task response.")]
        };
    }

    [Fact]
    public async Task SaveRunAsync_WhenConfidential_ProtectsEveryArtifactAndRoundTrips()
    {
        var protector = new PassThroughContentProtector();
        var (repository, catalog) = CreateRepository(contentProtector: protector);
        var task = CreateTask("Confidential Run");
        await repository.SaveTaskAsync(task);
        var run = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow,
            ResultSummary = "confidential-summary",
            Error = "confidential-error",
            SystemInstruction = "confidential-instruction",
            SessionElement = JsonSerializer.SerializeToElement(new { value = "confidential-session" }),
            TriggerParameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["input"] = JsonSerializer.SerializeToElement("confidential-parameter")
            },
            LiveMessages = [new ChatMessage(ChatRole.Assistant, "confidential-message")]
        };

        await repository.SaveRunAsync(run);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id)!;
        var runsDirectory = Path.Combine(taskDirectory, "runs");
        var raw = string.Join(
            Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(runsDirectory, "index.jsonl")),
            await File.ReadAllTextAsync(Path.Combine(runsDirectory, run.Id + ".jsonl")),
            await File.ReadAllTextAsync(Path.Combine(runsDirectory, run.Id + ".session.json.enc")));
        foreach (var secret in new[]
                 {
                     "confidential-summary",
                     "confidential-error",
                     "confidential-instruction",
                     "confidential-session",
                     "confidential-parameter",
                     "confidential-message"
                 })
        {
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        }

        var loaded = await repository.GetRunAsync(_userId, task.Id, run.Id);

        Assert.NotNull(loaded);
        Assert.Equal("confidential-summary", loaded.ResultSummary);
        Assert.Equal("confidential-error", loaded.Error);
        Assert.Equal("confidential-instruction", loaded.SystemInstruction);
        Assert.Equal("confidential-session", loaded.SessionElement?.GetProperty("value").GetString());
        Assert.Equal("confidential-parameter", loaded.TriggerParameters["input"].GetString());
        Assert.Equal("confidential-message", Assert.Single(loaded.LiveMessages!).Text);
    }

    [Fact]
    public async Task GetLatestSessionAsync_SkipsOnlySessionlessSkippedRunsAndReadsOnlyOwnedSession()
    {
        var protector = new PassThroughContentProtector();
        var (repository, _) = CreateRepository(contentProtector: protector);
        var task = CreateTask("Persistent Conversation");
        await repository.SaveTaskAsync(task);
        var startedAt = DateTimeOffset.UtcNow;
        var completed = CreateRun("completed", task, "private summary", "private response", "earlier-session");
        completed.StartedAt = startedAt;
        await repository.SaveRunAsync(completed);
        for (var occurrence = 1; occurrence <= 2; occurrence++)
        {
            await repository.SaveRunAsync(new AgentTaskRun
            {
                Id = "skipped-" + occurrence,
                TaskId = task.Id,
                UserId = task.UserId,
                Status = AgentTaskStatus.Skipped,
                StartedAt = startedAt.AddMinutes(occurrence)
            });
        }

        Assert.Equal(AgentTaskStatus.Skipped, (await repository.GetLatestRunAsync(task.UserId, task.Id))?.Status);
        Assert.Null(await repository.GetLatestSessionAsync("another-user", task.Id));
        Assert.DoesNotContain(protector.UnprotectCalls, call => call.UserId == "another-user");
        var session = await repository.GetLatestSessionAsync(task.UserId, task.Id);
        Assert.Equal("earlier-session", session?.GetProperty("value").GetString());
        var opened = Assert.Single(protector.UnprotectCalls, call => call.Purpose.EndsWith("\0session", StringComparison.Ordinal));
        Assert.Equal(task.UserId, opened.UserId);
        Assert.Equal($"task\0{task.Id}\0{completed.Id}\0session", opened.Purpose);

        var skippedWithSession = CreateRun("skipped-with-session", task, "", "", "newer-session");
        skippedWithSession.Status = AgentTaskStatus.Skipped;
        skippedWithSession.StartedAt = startedAt.AddMinutes(3);
        await repository.SaveRunAsync(skippedWithSession);
        session = await repository.GetLatestSessionAsync(task.UserId, task.Id);
        Assert.Equal("newer-session", session?.GetProperty("value").GetString());

        await repository.SaveRunAsync(new AgentTaskRun
        {
            Id = "sessionless-execution",
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Failed,
            StartedAt = startedAt.AddMinutes(4)
        });
        Assert.Null(await repository.GetLatestSessionAsync(task.UserId, task.Id));
        Assert.Equal(2, protector.UnprotectCalls.Count(call => call.Purpose.EndsWith("\0session", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetRunMetadataAsync_WhenConfidential_DoesNotOpenProtectedContent()
    {
        var protector = new PassThroughContentProtector();
        var (repository, _) = CreateRepository(contentProtector: protector);
        var task = CreateTask("Confidential Metadata");
        await repository.SaveTaskAsync(task);
        var run = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            ResultSummary = "hidden-summary"
        };
        await repository.SaveRunAsync(run);
        var callsBeforeRead = protector.UnprotectCalls.Count;

        var metadata = await repository.GetRunMetadataAsync(_userId, task.Id, run.Id);

        Assert.NotNull(metadata);
        Assert.Null(metadata.ResultSummary);
        Assert.Null(metadata.Error);
        var opened = Assert.Single(protector.UnprotectCalls.Skip(callsBeforeRead));
        Assert.Equal($"task\0{task.Id}\0index", opened.Purpose);
    }

    /// <summary>Run summaries carry the result and error from the index without opening transcripts or sessions.</summary>
    [Fact]
    public async Task GetRunSummariesAsync_ReturnsResultAndErrorWithoutOpeningTranscripts()
    {
        // Arrange
        var protector = new PassThroughContentProtector();
        var (repository, _) = CreateRepository(contentProtector: protector);
        var task = CreateTask("Run Summaries");
        await repository.SaveTaskAsync(task);
        var startedAt = DateTimeOffset.UtcNow;
        await repository.SaveRunAsync(new AgentTaskRun
        {
            Id = "older", TaskId = task.Id, UserId = task.UserId, Status = AgentTaskStatus.Failed,
            StartedAt = startedAt, Error = "disk full", LiveMessages = [new(ChatRole.Assistant, "older transcript")]
        });
        await repository.SaveRunAsync(new AgentTaskRun
        {
            Id = "newer", TaskId = task.Id, UserId = task.UserId, Status = AgentTaskStatus.Completed,
            StartedAt = startedAt.AddMinutes(1), ResultSummary = "wrote report.md",
            LiveMessages = [new(ChatRole.Assistant, "newer transcript")]
        });
        var callsBeforeRead = protector.UnprotectCalls.Count;

        // Act
        var runs = await repository.GetRunSummariesAsync(_userId, task.Id, limit: 10);

        // Assert
        Assert.Equal(["newer", "older"], runs.Select(run => run.Id));
        Assert.Equal("wrote report.md", runs[0].ResultSummary);
        Assert.Equal("disk full", runs[1].Error);
        Assert.All(runs, run => Assert.Null(run.LiveMessages));
        Assert.All(protector.UnprotectCalls.Skip(callsBeforeRead), opened => Assert.Equal($"task\0{task.Id}\0index", opened.Purpose));
        Assert.Empty(await repository.GetRunSummariesAsync("someone-else", task.Id, limit: 10));
    }

    [Theory]
    [InlineData("missing-payload")]
    [InlineData("unopenable-payload")]
    [InlineData("invalid-json")]
    public async Task GetRunAsync_WhenEncryptedMessageIsCorrupt_Throws(string corruption)
    {
        var protector = new PassThroughContentProtector();
        var (repository, catalog) = CreateRepository(contentProtector: protector);
        var task = CreateTask("Corrupt Confidential Run");
        await repository.SaveTaskAsync(task);
        var run = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            LiveMessages = [new ChatMessage(ChatRole.User, "secret")]
        };
        await repository.SaveRunAsync(run);
        var path = Path.Combine(catalog.TryGetTaskDirectory(task.Id)!, "runs", run.Id + ".jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        lines[1] = corruption switch
        {
            "missing-payload" => "{}",
            "unopenable-payload" => "invalid",
            _ => protector.Protect(_userId, $"task\0{task.Id}\0{run.Id}\0transcript", "not-json")
        };
        await File.WriteAllLinesAsync(path, lines);

        if (corruption == "invalid-json")
        {
            var loaded = await repository.GetRunAsync(_userId, task.Id, run.Id);
            Assert.NotNull(loaded);
            Assert.Null(loaded!.LiveMessages);
            return;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetRunAsync(_userId, task.Id, run.Id));
    }

    [Fact]
    public async Task SaveRunAsync_WhenNoSession_OmitsSessionSidecar()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Daily Inbox Triage");
        await repository.SaveTaskAsync(task);
        var run = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed
        };

        await repository.SaveRunAsync(run);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var runsDirectory = Path.Combine(taskDirectory!, "runs");
        Assert.False(File.Exists(Path.Combine(runsDirectory, run.Id + ".session.json.enc")));
    }

    [Fact]
    public async Task SaveRunAsync_WhenSessionIsCleared_RemovesStaleSessionSidecar()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Daily Inbox Triage");
        await repository.SaveTaskAsync(task);
        var run = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            SessionElement = JsonSerializer.SerializeToElement(new { value = "stale" })
        };
        await repository.SaveRunAsync(run);
        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var sessionPath = Path.Combine(taskDirectory!, "runs", run.Id + ".session.json.enc");
        Assert.True(File.Exists(sessionPath));

        run.SessionElement = null;
        await repository.SaveRunAsync(run);

        Assert.False(File.Exists(sessionPath));
        Assert.Null((await repository.GetRunAsync(_userId, task.Id, run.Id))!.SessionElement);
    }

    [Fact]
    public async Task SaveRunAsync_WhenRunIdEscapesRunsDirectory_RejectsWrite()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Contained Run Task");
        await repository.SaveTaskAsync(task);
        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var runsDirectory = Path.Combine(taskDirectory!, "runs");
        var outsideStem = _usersRoot + "-outside-run";
        var run = new AgentTaskRun
        {
            Id = Path.GetRelativePath(runsDirectory, outsideStem),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            SessionElement = JsonDocument.Parse("{\"escaped\":true}").RootElement.Clone()
        };

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveRunAsync(run));

            Assert.False(File.Exists(outsideStem + ".jsonl"));
            Assert.False(File.Exists(outsideStem + ".session.json.enc"));
        }
        finally
        {
            File.Delete(outsideStem + ".jsonl");
            File.Delete(outsideStem + ".session.json.enc");
        }
    }

    [Fact]
    public async Task SaveRunAsync_WhenTaskIdIsReusedByAnotherUser_DoesNotPersistRunForReplacementOwner()
    {
        var (repository, _) = CreateRepository();
        var originalTask = CreateTask("Original Owner Task");
        await repository.SaveTaskAsync(originalTask);
        await repository.DeleteTaskAsync(originalTask.UserId, originalTask.Id);

        var replacementUserId = Guid.NewGuid().ToString();
        var replacementTask = CreateTask("Replacement Owner Task");
        replacementTask.Id = originalTask.Id;
        replacementTask.UserId = replacementUserId;
        await repository.SaveTaskAsync(replacementTask);

        await repository.SaveRunAsync(new AgentTaskRun
        {
            UserId = originalTask.UserId,
            TaskId = originalTask.Id,
            Status = AgentTaskStatus.Completed,
            ResultSummary = "Original owner's private result."
        });

        Assert.Empty(await repository.GetRunsAsync(replacementUserId, replacementTask.Id));
    }

    [Fact]
    public async Task SaveRunAsync_RedactsCapturedRunContentBeforePersisting()
    {
        var (repository, catalog) = CreateRepository(CreateRedactionService());
        var task = CreateTask("Daily Inbox Triage");
        await repository.SaveTaskAsync(task);
        var run = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            LiveMessages =
            [
                new ChatMessage(ChatRole.Assistant, "Task saw stored-secret-value.")
            ],
            SystemInstruction = "Use stored-secret-value carefully.",
            ResultSummary = "Summary includes stored-secret-value.",
            Error = "Failure includes stored-secret-value.",
            TriggerParameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["payload"] = JsonSerializer.SerializeToElement("Trigger includes stored-secret-value.")
            }
        };

        await repository.SaveRunAsync(run);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var runsDirectory = Path.Combine(taskDirectory!, "runs");
        var indexLine = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(runsDirectory, "index.jsonl")));
        var transcript = await File.ReadAllTextAsync(Path.Combine(runsDirectory, run.Id + ".jsonl"));
        Assert.DoesNotContain("stored-secret-value", indexLine, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-secret-value", transcript, StringComparison.Ordinal);

        var loaded = await repository.GetRunAsync(_userId, task.Id, run.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Task saw ***.", loaded!.LiveMessages!.Single().Text);
        Assert.Equal("Use *** carefully.", loaded.SystemInstruction);
        Assert.Equal("Summary includes ***.", loaded.ResultSummary);
        Assert.Equal("Failure includes ***.", loaded.Error);
        Assert.Equal("Trigger includes ***.", loaded.TriggerParameters["payload"].GetString());
    }

    [Fact]
    public async Task SaveRunAsync_WithConcurrentReplacement_CoordinatesAllRunArtifacts()
    {
        var redactionService = new BlockingRunRedactionService();
        var (repository, _) = CreateRepository(redactionService);
        var task = CreateTask("Concurrent Replacement");
        await repository.SaveTaskAsync(task);
        var runId = Guid.NewGuid().ToString();
        var firstRun = CreateRun(runId, task, "First summary.", "First response.", "first");
        var secondRun = CreateRun(runId, task, "Second summary.", "Second response.", "second");

        var firstSave = repository.SaveRunAsync(firstRun);
        await redactionService.FirstIndexBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondSave = Task.CompletedTask;

        try
        {
            secondSave = repository.SaveRunAsync(secondRun);
            Assert.False(redactionService.SecondTranscriptStarted.Task.IsCompleted);
        }
        finally
        {
            redactionService.ReleaseFirstIndex.SetResult();
            await Task.WhenAll(firstSave, secondSave).WaitAsync(TimeSpan.FromSeconds(5));
        }

        var loaded = await repository.GetRunAsync(task.UserId, task.Id, runId);
        Assert.NotNull(loaded);
        Assert.Equal("Second summary.", loaded!.ResultSummary);
        Assert.Equal("Second response.", loaded.LiveMessages!.Single().Text);
        Assert.Equal("second", loaded.SessionElement!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task SaveAndGetTaskAsync_RoundTripsToolApprovalRunMode()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Approval-Sensitive Task");
        task.ToolApprovalRunMode = ToolApprovalRunMode.AutoApproveRequired;
        task.CreatedByAgentId = "agent-creator";

        await repository.SaveTaskAsync(task);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var taskFile = (await EncryptedFileContent.ReadAllTextAsync(_userId, Path.Combine(taskDirectory!, "task.md.enc"), new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector()))!;
        Assert.Contains("tool_approval_run_mode: auto_approve_required", taskFile);
        Assert.Contains("created_by_agent_id: agent-creator", taskFile);

        var loaded = await repository.GetTaskAsync(_userId, task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(ToolApprovalRunMode.AutoApproveRequired, loaded!.ToolApprovalRunMode);
        Assert.Equal("agent-creator", loaded.CreatedByAgentId);
    }

    [Fact]
    public async Task SaveAndGetTaskAsync_RoundTripsAfterTaskSchedule()
    {
        var (repository, catalog) = CreateRepository();
        var predecessorTaskId = Guid.NewGuid().ToString();
        var task = CreateTask("Dependent Task");
        task.Schedule = new TaskSchedule
        {
            Kind = TaskScheduleKind.AfterTask,
            PredecessorTaskId = $" {predecessorTaskId} "
        };

        await repository.SaveTaskAsync(task);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var taskFile = (await EncryptedFileContent.ReadAllTextAsync(_userId, Path.Combine(taskDirectory!, "task.md.enc"), new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector()))!;
        Assert.Contains("kind: after_task", taskFile, StringComparison.Ordinal);
        Assert.Contains($"predecessor_task_id: {predecessorTaskId}", taskFile, StringComparison.Ordinal);

        var loaded = await repository.GetTaskAsync(_userId, task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(TaskScheduleKind.AfterTask, loaded!.Schedule.Kind);
        Assert.Equal(predecessorTaskId, loaded.Schedule.PredecessorTaskId);
    }

    [Fact]
    public async Task GetTaskAsync_WhenToolApprovalRunModeMissing_DefaultsToAutoRejectRequired()
    {
        var catalog = CreateCatalog();
        var repository = new AgentTaskRepository(
            () => _usersRoot,
            catalog,
            _yamlConverter,
            NullLogger<AgentTaskRepository>.Instance,
            new FileSessionIndexCoordinator(),
            new PassThroughContentProtector());
        var taskId = Guid.NewGuid().ToString();
        await WriteTaskFileAsync(_userId, "legacy-task", taskId, "Legacy Task");
        await catalog.ReloadAsync();

        var loaded = await repository.GetTaskAsync(_userId, taskId);

        Assert.NotNull(loaded);
        Assert.Equal(ToolApprovalRunMode.AutoRejectRequired, loaded!.ToolApprovalRunMode);
        Assert.Null(loaded.CreatedByAgentId);
    }

    [Fact]
    public async Task SaveTaskAsync_WithRunScope_RoundTripsCustomRuntimeSurface()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Scoped Reflection");
        task.RunScope = new TaskRunScope
        {
            AllowedToolNames = ["file__read", "file__write"],
            ToolPolicies =
            [
                new AgentToolPolicy
                {
                    ToolName = "file__write",
                    ApprovalMode = ToolApprovalMode.AutoApprove
                },
                new AgentToolPolicy
                {
                    ToolName = "file__read",
                    ApprovalMode = ToolApprovalMode.Default
                }
            ],
            IsToolSearchEnabled = true,
            PinnedToolNames = ["file__read"],
            IsCompactionEnabled = false,
            IsMemoryEnabled = false,
            IsTodoEnabled = true,
            IsSubagentDelegationEnabled = true,
            IsLocalOnly = true,
            AgentToolIds = ["agent-tool-1"],
            AllowedSkillNames = ["note-review"],
            AllowedWorkspaces = [],
            AllowedDirectories =
            [
                new AllowedDirectory(
                    "agents/main/private/notes",
                    "notes",
                    DirectorySourceKind.UserDirectory,
                    WorkspaceAccessMode.ReadWrite)
            ]
        };

        await repository.SaveTaskAsync(task);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var taskFile = (await EncryptedFileContent.ReadAllTextAsync(_userId, Path.Combine(taskDirectory!, "task.md.enc"), new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector()))!;
        Assert.Contains("run_scope:", taskFile, StringComparison.Ordinal);
        Assert.Contains("allowed_workspaces: []", taskFile, StringComparison.Ordinal);
        Assert.DoesNotContain("approval_mode: default", taskFile, StringComparison.OrdinalIgnoreCase);

        var loaded = await repository.GetTaskAsync(_userId, task.Id);

        Assert.NotNull(loaded?.RunScope);
        Assert.Equal(["file__read", "file__write"], loaded!.RunScope!.AllowedToolNames);
        Assert.True(loaded.RunScope.IsToolSearchEnabled);
        Assert.False(loaded.RunScope.IsMemoryEnabled);
        Assert.True(loaded.RunScope.IsTodoEnabled);
        Assert.True(loaded.RunScope.IsSubagentDelegationEnabled);
        Assert.True(loaded.RunScope.IsLocalOnly);
        Assert.Equal(["file__read"], loaded.RunScope.PinnedToolNames);
        Assert.Equal(["agent-tool-1"], loaded.RunScope.AgentToolIds);
        Assert.Equal(["note-review"], loaded.RunScope.AllowedSkillNames);
        Assert.Empty(loaded.RunScope.AllowedWorkspaces);
        var policy = Assert.Single(loaded.RunScope.ToolPolicies);
        Assert.Equal("file__write", policy.ToolName);
        Assert.Equal(ToolApprovalMode.AutoApprove, policy.ApprovalMode);
        var directory = Assert.Single(loaded.RunScope.AllowedDirectories);
        Assert.Equal("agents/main/private/notes", directory.Value);
        Assert.Equal("notes", directory.Label);
        Assert.Equal(DirectorySourceKind.UserDirectory, directory.Kind);
        Assert.Equal(WorkspaceAccessMode.ReadWrite, directory.AccessMode);
    }

    [Fact]
    public async Task SaveTaskAsync_WithNullRunScope_OmitsRunScopeBlock()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Inherited Scope");

        await repository.SaveTaskAsync(task);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var taskFile = (await EncryptedFileContent.ReadAllTextAsync(_userId, Path.Combine(taskDirectory!, "task.md.enc"), new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector()))!;
        Assert.DoesNotContain("run_scope:", taskFile, StringComparison.Ordinal);

        var loaded = await repository.GetTaskAsync(_userId, task.Id);
        Assert.Null(loaded?.RunScope);
    }

    [Fact]
    public async Task ReloadAsync_WhenTaskIdIsDuplicated_SkipsDuplicateTask()
    {
        var duplicateTaskId = Guid.NewGuid().ToString();
        const string firstUserId = "00000000-0000-0000-0000-000000000001";
        const string otherUserId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        var catalog = CreateCatalog();

        await WriteTaskFileAsync(firstUserId, "first-task", duplicateTaskId, "First Task");
        await WriteTaskFileAsync(otherUserId, "imported-task", duplicateTaskId, "Imported Duplicate");

        await catalog.ReloadAsync();

        Assert.Single(catalog.GetTasksForUser(firstUserId));
        Assert.Empty(catalog.GetTasksForUser(otherUserId));
        Assert.Equal(firstUserId, catalog.TryGetOwnerUserId(duplicateTaskId));
    }

    [Fact]
    public async Task GetRunsAsync_WhenRunLineIsMalformed_ReadsAndSavesValidRunsOnly()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Daily Inbox Triage");
        await repository.SaveTaskAsync(task);

        var run = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            ResultSummary = "Valid run."
        };

        await repository.SaveRunAsync(run);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);

        var runsDirectory = Path.Combine(taskDirectory!, "runs");
        await File.AppendAllTextAsync(Path.Combine(runsDirectory, "index.jsonl"), new PassThroughContentProtector().Protect(_userId, $"task\0{task.Id}\0index", "not json") + Environment.NewLine);

        var laterRun = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ResultSummary = "Later valid run."
        };

        await repository.SaveRunAsync(laterRun);

        var latestRun = await repository.GetLatestRunAsync(_userId, task.Id);
        var runs = await repository.GetRunsAsync(_userId, task.Id);

        Assert.NotNull(latestRun);
        Assert.Equal(laterRun.Id, latestRun!.Id);
        Assert.Equal([laterRun.Id, run.Id], runs.Select(currentRun => currentRun.Id));
    }

    [Fact]
    public async Task GetRunsAsync_WhenRunIndexHasInvalidTimestamp_SkipsBadRow()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Daily Inbox Triage");
        await repository.SaveTaskAsync(task);
        var validRun = new AgentTaskRun
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            ResultSummary = "Valid run."
        };
        await repository.SaveRunAsync(validRun);

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var indexPath = Path.Combine(taskDirectory!, "runs", "index.jsonl");
        await File.AppendAllTextAsync(
            indexPath,
            new PassThroughContentProtector().Protect(_userId, $"task\0{task.Id}\0index", "{\"id\":\"bad-run\",\"task_id\":\"" + task.Id + "\",\"status\":\"completed\",\"started_at\":\"not-a-timestamp\"}") + Environment.NewLine);

        var runs = await repository.GetRunsAsync(_userId, task.Id);

        var loaded = Assert.Single(runs);
        Assert.Equal(validRun.Id, loaded.Id);
    }

    [Fact]
    public async Task SaveRunAsync_WithConcurrentRuns_PreservesAllIndexRows()
    {
        var (repository, catalog) = CreateRepository();
        var task = CreateTask("Concurrent Task");
        await repository.SaveTaskAsync(task);
        var runs = Enumerable.Range(0, 8)
            .Select(index => new AgentTaskRun
            {
                Id = Guid.NewGuid().ToString(),
                TaskId = task.Id,
                UserId = task.UserId,
                Status = AgentTaskStatus.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(index)
            })
            .ToList();

        await Task.WhenAll(runs.Select(run => repository.SaveRunAsync(run)));

        var taskDirectory = catalog.TryGetTaskDirectory(task.Id);
        Assert.NotNull(taskDirectory);
        var indexPath = Path.Combine(taskDirectory!, "runs", "index.jsonl");
        var indexLines = await File.ReadAllLinesAsync(indexPath);
        Assert.Equal(runs.Count, indexLines.Length);

        var loadedRuns = await repository.GetRunsAsync(_userId, task.Id, limit: 20);
        Assert.Equal(runs.Count, loadedRuns.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_usersRoot))
        {
            Directory.Delete(_usersRoot, recursive: true);
        }
    }

    private (AgentTaskRepository Repository, AgentTaskCatalog Catalog) CreateRepository(
        IRedactionService? redactionService = null,
        IEnumerable<string>? reservedTaskSlugs = null,
        IContentProtector? contentProtector = null)
    {
        var catalog = CreateCatalog();
        var repository = new AgentTaskRepository(
            () => _usersRoot,
            catalog,
            _yamlConverter,
            NullLogger<AgentTaskRepository>.Instance,
            new FileSessionIndexCoordinator(),
            contentProtector ?? new PassThroughContentProtector(),
            redactionService,
            reservedTaskSlugs);
        return (repository, catalog);
    }

    private AgentTaskRepository CreateRepository(IAgentTaskCatalog catalog)
    {
        return new AgentTaskRepository(
            () => _usersRoot,
            catalog,
            _yamlConverter,
            NullLogger<AgentTaskRepository>.Instance,
            new FileSessionIndexCoordinator(),
            new PassThroughContentProtector());
    }

    private AgentTaskCatalog CreateCatalog()
    {
        return new AgentTaskCatalog(
            () => _usersRoot,
            _yamlConverter,
            NullLogger<AgentTaskCatalog>.Instance,
            new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector());
    }

    private static IRedactionService CreateRedactionService()
    {
        return new ReplacingRedactionService();
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
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static AgentTaskRun CreateRun(
        string runId,
        AgentTaskDefinition task,
        string summary,
        string response,
        string sessionValue)
    {
        return new AgentTaskRun
        {
            Id = runId,
            TaskId = task.Id,
            UserId = task.UserId,
            Status = AgentTaskStatus.Completed,
            ResultSummary = summary,
            LiveMessages = [new ChatMessage(ChatRole.Assistant, response)],
            SessionElement = JsonSerializer.SerializeToElement(new { value = sessionValue })
        };
    }

    private async Task WriteTaskFileAsync(string userId, string folderName, string taskId, string name)
    {
        var taskDirectory = Path.Combine(_usersRoot, userId, "tasks", folderName);
        Directory.CreateDirectory(taskDirectory);

        var taskFile = new AgentTaskDefinitionFile
        {
            Id = taskId,
            Name = name,
            AgentId = Guid.NewGuid().ToString(),
            Schedule = new ScheduleFile
            {
                Kind = "interval",
                IntervalMinutes = 30
            },
            IsEnabled = true,
            NotifyOnCompletion = false,
            SessionMode = "isolated",
            CreatedAt = IsoTimestamp.FormatSeconds(DateTimeOffset.UtcNow)
        };

        var yaml = _yamlConverter.Serialize(taskFile);
        var content = MarkdownFrontmatter.Combine(yaml, "Summarize the queue.");
        await EncryptedFileContent.WriteAllTextAsync(userId, Path.Combine(taskDirectory, "task.md.enc"), content, new Oscova.Agents.Tests.Infrastructure.TestDoubles.PassThroughContentProtector()).ConfigureAwait(false);
    }

    private sealed class ReplacingRedactionService : IRedactionService
    {
        public Task<string> RedactTextAsync(string text, string? userId = null, CancellationToken ct = default)
            => Task.FromResult(text.Replace("stored-secret-value", "***", StringComparison.Ordinal));

        public Task<IDictionary<string, object?>> RedactArgumentsAsync(
            IDictionary<string, object?> arguments,
            string? userId = null,
            CancellationToken ct = default)
            => Task.FromResult<IDictionary<string, object?>>(arguments.ToDictionary(
                pair => pair.Key,
                pair => pair.Value is JsonElement { ValueKind: JsonValueKind.String } element
                    ? element.GetString()?.Replace("stored-secret-value", "***", StringComparison.Ordinal)
                    : pair.Value,
                StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<ChatMessage>> RedactMessagesAsync(
            IReadOnlyList<ChatMessage> messages,
            string? userId = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChatMessage>>(
                [.. messages.Select(message => new ChatMessage(message.Role, message.Text?.Replace("stored-secret-value", "***", StringComparison.Ordinal)))]);
    }

    private sealed class BlockingRunRedactionService : IRedactionService
    {
        public TaskCompletionSource FirstIndexBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstIndex { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondTranscriptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> RedactTextAsync(string text, string? userId = null, CancellationToken ct = default)
        {
            if (string.Equals(text, "First summary.", StringComparison.Ordinal))
            {
                FirstIndexBlocked.SetResult();
                await ReleaseFirstIndex.Task.WaitAsync(ct);
            }

            return text;
        }

        public Task<IDictionary<string, object?>> RedactArgumentsAsync(
            IDictionary<string, object?> arguments,
            string? userId = null,
            CancellationToken ct = default) => Task.FromResult(arguments);

        public Task<IReadOnlyList<ChatMessage>> RedactMessagesAsync(
            IReadOnlyList<ChatMessage> messages,
            string? userId = null,
            CancellationToken ct = default)
        {
            if (messages.Any(message => string.Equals(message.Text, "Second response.", StringComparison.Ordinal)))
            {
                SecondTranscriptStarted.SetResult();
            }

            return Task.FromResult(messages);
        }
    }

    private sealed class CancelOnReloadAgentTaskCatalog(IAgentTaskCatalog inner) : IAgentTaskCatalog
    {
        private CancellationTokenSource? _cancellation;
        public Action? TasksEnumerated { get; init; }

        public string? TryGetTaskDirectory(string taskId) => inner.TryGetTaskDirectory(taskId);

        public string? TryGetOwnerUserId(string taskId) => inner.TryGetOwnerUserId(taskId);

        public string? TryGetHandlerKey(string taskId) => inner.TryGetHandlerKey(taskId);

        public IReadOnlyCollection<AgentTaskCatalogEntry> GetTasksForUser(string userId)
        {
            var snapshot = inner.GetTasksForUser(userId);
            TasksEnumerated?.Invoke();
            return snapshot;
        }

        public IReadOnlyCollection<AgentTaskCatalogEntry> GetAllEnabledTasks() => inner.GetAllEnabledTasks();

        public Task ReloadAsync(CancellationToken ct = default)
        {
            _cancellation?.Cancel();
            _cancellation = null;
            return inner.ReloadAsync(ct);
        }

        public Task ReloadUserAsync(string userId, CancellationToken ct = default) => inner.ReloadUserAsync(userId, ct);

        public void CancelOnNextReload(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }
    }
}
