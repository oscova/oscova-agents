using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;
using Oscova.Agents.Persistence;
using Oscova.Agents.Policies;
using Oscova.Agents.Secrets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tasks;

/// <summary>Persists task definitions and run artifacts beneath a caller-selected users root.</summary>
public sealed class AgentTaskRepository(
    Func<string> usersRootResolver,
    IAgentTaskCatalog catalog,
    YamlConverter yamlCodec,
    ILogger<AgentTaskRepository> logger,
    FileSessionIndexCoordinator indexCoordinator,
    IContentProtector contentProtector,
    IRedactionService? redactionService = null,
    IEnumerable<string>? reservedTaskSlugs = null) : IAgentTaskRepository
{
    private static string RunIndexPurpose(string taskId) => $"task\0{taskId}\0index";

    private readonly Func<string> _usersRootResolver = usersRootResolver
        ?? throw new ArgumentNullException(nameof(usersRootResolver));
    private readonly IReadOnlySet<string> _reservedTaskSlugs =
        new HashSet<string>(reservedTaskSlugs ?? [], StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions RunSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static string RunPurpose(string taskId, string runId, string artifact) =>
        $"task\0{taskId}\0{runId}\0{artifact}";

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentTaskDefinition>> GetAllEnabledTasksAsync(CancellationToken ct = default)
    {
        var tasks = catalog.GetAllEnabledTasks()
            .Where(entry => contentProtector.IsAvailable(entry.OwnerUserId))
            .Select(entry => ReadExistingTask(GetTaskFilePath(entry.DirectoryPath), entry.OwnerUserId))
            .OfType<AgentTaskDefinition>()
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentTaskDefinition>>(tasks);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentTaskDefinition>> GetTasksByUserAsync(string userId, CancellationToken ct = default)
    {
        var tasks = catalog.GetTasksForUser(userId)
            .Where(entry => contentProtector.IsAvailable(entry.OwnerUserId))
            .Select(entry => ReadExistingTask(GetTaskFilePath(entry.DirectoryPath), entry.OwnerUserId))
            .OfType<AgentTaskDefinition>()
            .OrderBy(task => task.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentTaskDefinition>>(tasks);
    }

    /// <inheritdoc />
    public Task<AgentTaskDefinition?> GetTaskAsync(string userId, string taskId, CancellationToken ct = default)
    {
        var taskDirectory = catalog.TryGetTaskDirectory(taskId);
        var ownerUserId = catalog.TryGetOwnerUserId(taskId);
        if (taskDirectory is null || ownerUserId is null || !IsOwnedBy(ownerUserId, userId) || !contentProtector.IsAvailable(ownerUserId))
        {
            return Task.FromResult<AgentTaskDefinition?>(null);
        }

        return Task.FromResult(ReadExistingTask(GetTaskFilePath(taskDirectory), ownerUserId));
    }

    /// <inheritdoc />
    public Task<AgentTaskDefinition?> GetTaskByExternalTriggerSlugAsync(string slug, CancellationToken ct = default)
    {
        var normalizedSlug = Slugs.ForTaskTrigger(slug);
        var matches = new List<AgentTaskDefinition>();

        foreach (var taskFilePath in EnumerateTaskFiles())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var ownerUserId = GetOwnerUserIdFromTaskFilePath(taskFilePath);
                if (string.IsNullOrWhiteSpace(ownerUserId) || !contentProtector.IsAvailable(ownerUserId) ||
                    !normalizedSlug.StartsWith(Slugs.ForTaskTrigger(ownerUserId) + "-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var task = ReadFromDisk(taskFilePath, ownerUserId);
                if (task.ExternalTrigger.IsEnabled &&
                    string.Equals(task.ExternalTrigger.Slug, normalizedSlug, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(task);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or JsonException)
            {
                logger.LogWarning(ex, "Skipping task file '{TaskFilePath}' while resolving external trigger slug '{Slug}'.", taskFilePath, normalizedSlug);
            }
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException($"External task trigger slug '{normalizedSlug}' is used by more than one task.");
        }

        return Task.FromResult(matches.SingleOrDefault());
    }

    /// <summary>Seeds a missing task in a caller-selected directory while preserving an existing definition.</summary>
    public async Task EnsureTaskAsync(string directoryName, AgentTaskDefinition task, CancellationToken ct = default)
    {
        if (!PathHelper.IsSafePathSegment(directoryName))
        {
            throw new ArgumentException("Task directory must be a safe path segment.", nameof(directoryName));
        }
        var root = GetTasksRoot(task.UserId);
        if (!PathHelper.TryResolveContainedPath(root, directoryName, out var directory))
        {
            throw new UnauthorizedAccessException("Task directory escapes the user's task root.");
        }
        if (File.Exists(GetTaskFilePath(directory)))
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        WriteToDisk(directory, task);
        await catalog.ReloadUserAsync(task.UserId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveTaskAsync(AgentTaskDefinition task, CancellationToken ct = default, string? creationDirectoryName = null)
    {
        var currentDirectory = catalog.TryGetTaskDirectory(task.Id);
        string ownerUserId;
        string targetDirectory;

        if (currentDirectory is null)
        {
            ownerUserId = task.UserId;
            var tasksRoot = GetTasksRoot(ownerUserId);
            if (creationDirectoryName is not null)
            {
                if (!PathHelper.IsSafePathSegment(creationDirectoryName))
                {
                    throw new ArgumentException("The new task directory must be a safe path segment.", nameof(creationDirectoryName));
                }
                if (!PathHelper.TryResolveContainedPath(tasksRoot, creationDirectoryName, out targetDirectory))
                {
                    throw new UnauthorizedAccessException("The task directory escapes the user's task root.");
                }
            }
            else
            {
                var existingSlugs = GetExistingSlugs(tasksRoot);
                var slug = Slugs.MakeUnique(Slugs.ForAgentFolder(task.Name), existingSlugs);
                targetDirectory = Path.Combine(tasksRoot, slug);
            }
        }
        else
        {
            ownerUserId = catalog.TryGetOwnerUserId(task.Id)
                ?? throw new InvalidOperationException($"Task '{task.Id}' is missing an owner in the catalog.");

            if (!IsOwnedBy(ownerUserId, task.UserId))
            {
                throw new InvalidOperationException("Task owner mismatch.");
            }

            targetDirectory = currentDirectory;
        }

        task.UserId = ownerUserId;
        WriteToDisk(targetDirectory, task);
        await catalog.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteTaskAsync(string userId, string taskId, CancellationToken ct = default)
    {
        var taskDirectory = catalog.TryGetTaskDirectory(taskId);
        var ownerUserId = catalog.TryGetOwnerUserId(taskId);
        if (taskDirectory is null || !IsOwnedBy(ownerUserId, userId))
        {
            return;
        }

        try
        {
            await DeleteTaskDirectoryWithRetryAsync(taskDirectory, ct).ConfigureAwait(false);
        }
        finally
        {
            await catalog.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<AgentTaskRun?> GetLatestRunAsync(string userId, string taskId, CancellationToken ct = default)
    {
        var taskDirectory = catalog.TryGetTaskDirectory(taskId);
        var ownerUserId = catalog.TryGetOwnerUserId(taskId);
        if (taskDirectory is null || !IsOwnedBy(ownerUserId, userId))
        {
            return Task.FromResult<AgentTaskRun?>(null);
        }

        return Task.FromResult(ReadLatestRun(taskDirectory, taskId, ownerUserId!, includeContent: true));
    }

    /// <inheritdoc />
    public Task<JsonElement?> GetLatestSessionAsync(string userId, string taskId, CancellationToken ct = default)
    {
        var taskDirectory = catalog.TryGetTaskDirectory(taskId);
        var ownerUserId = catalog.TryGetOwnerUserId(taskId);
        if (taskDirectory is null || !IsOwnedBy(ownerUserId, userId))
        {
            return Task.FromResult<JsonElement?>(null);
        }

        var runsDirectory = GetRunsDirectory(taskDirectory);
        var indexPath = GetRunsIndexPath(runsDirectory);
        if (!File.Exists(indexPath)) return Task.FromResult<JsonElement?>(null);

        // A skipped occurrence with no session must not erase the last execution's context.
        var row = ReadRunIndexRows(indexPath, taskId, ownerUserId!)
            .OrderByDescending(run => string.IsNullOrWhiteSpace(run.StartedAt)
                ? DateTimeOffset.MinValue : IsoTimestamp.Parse(run.StartedAt))
            .FirstOrDefault(run => ParseEnum(run.Status, AgentTaskStatus.Pending) != AgentTaskStatus.Skipped ||
                File.Exists(GetRunSessionPath(runsDirectory, run.Id)));
        return Task.FromResult(row is null ? null : ReadRunSession(runsDirectory, row.Id, taskId, ownerUserId!));
    }

    /// <inheritdoc />
    public Task<AgentTaskRun?> GetLatestRunMetadataAsync(
        string userId,
        string taskId,
        CancellationToken ct = default)
    {
        var taskDirectory = catalog.TryGetTaskDirectory(taskId);
        var ownerUserId = catalog.TryGetOwnerUserId(taskId);
        if (taskDirectory is null || !IsOwnedBy(ownerUserId, userId))
        {
            return Task.FromResult<AgentTaskRun?>(null);
        }

        return Task.FromResult(ReadLatestRun(taskDirectory, taskId, ownerUserId!, includeContent: false));
    }

    /// <inheritdoc />
    public Task<AgentTaskRun?> GetRunAsync(string userId, string taskId, string runId, CancellationToken ct = default)
    {
        var taskDirectory = catalog.TryGetTaskDirectory(taskId);
        var ownerUserId = catalog.TryGetOwnerUserId(taskId);
        if (taskDirectory is null || !IsOwnedBy(ownerUserId, userId) || string.IsNullOrWhiteSpace(runId))
        {
            return Task.FromResult<AgentTaskRun?>(null);
        }

        var runsDirectory = GetRunsDirectory(taskDirectory);
        var indexPath = GetRunsIndexPath(runsDirectory);
        if (!File.Exists(indexPath))
        {
            return Task.FromResult<AgentTaskRun?>(null);
        }

        var indexRow = ReadRunIndexRows(indexPath, taskId, ownerUserId!)
            .FirstOrDefault(row => string.Equals(row.Id, runId, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(indexRow is null
            ? null
            : MapRunToDomain(indexRow, runsDirectory, taskId, ownerUserId!, includeContent: true));
    }

    /// <inheritdoc />
    public Task<AgentTaskRun?> GetRunMetadataAsync(
        string userId,
        string taskId,
        string runId,
        CancellationToken ct = default)
    {
        var taskDirectory = catalog.TryGetTaskDirectory(taskId);
        var ownerUserId = catalog.TryGetOwnerUserId(taskId);
        if (taskDirectory is null || !IsOwnedBy(ownerUserId, userId) || string.IsNullOrWhiteSpace(runId))
        {
            return Task.FromResult<AgentTaskRun?>(null);
        }

        var runsDirectory = GetRunsDirectory(taskDirectory);
        var indexPath = GetRunsIndexPath(runsDirectory);
        if (!File.Exists(indexPath))
        {
            return Task.FromResult<AgentTaskRun?>(null);
        }

        var indexRow = ReadRunIndexRows(indexPath, taskId, ownerUserId!)
            .FirstOrDefault(row => string.Equals(row.Id, runId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(indexRow is null
            ? null
            : MapRunToDomain(indexRow, runsDirectory, taskId, ownerUserId!, includeContent: false));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, AgentTaskRun>> GetLatestRunsAsync(IEnumerable<string> taskIds, CancellationToken ct = default)
    {
        var runs = new Dictionary<string, AgentTaskRun>(StringComparer.OrdinalIgnoreCase);
        foreach (var taskId in taskIds)
        {
            var taskDirectory = catalog.TryGetTaskDirectory(taskId);
            var ownerUserId = catalog.TryGetOwnerUserId(taskId);
            if (taskDirectory is null || ownerUserId is null)
            {
                continue;
            }

            var run = ReadLatestRun(taskDirectory, taskId, ownerUserId, includeContent: true);
            if (run is not null)
            {
                runs[taskId] = run;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, AgentTaskRun>>(runs);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentTaskRun>> GetRunsAsync(string userId, string taskId, int limit = 20, CancellationToken ct = default)
    {
        var taskDirectory = catalog.TryGetTaskDirectory(taskId);
        var ownerUserId = catalog.TryGetOwnerUserId(taskId);
        if (taskDirectory is null || !IsOwnedBy(ownerUserId, userId) || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<AgentTaskRun>>([]);
        }

        var runsDirectory = GetRunsDirectory(taskDirectory);
        var indexPath = GetRunsIndexPath(runsDirectory);
        if (!File.Exists(indexPath))
        {
            return Task.FromResult<IReadOnlyList<AgentTaskRun>>([]);
        }

        var runs = ReadRunIndexRows(indexPath, taskId, ownerUserId!)
            .OrderByDescending(row => string.IsNullOrWhiteSpace(row.StartedAt) ? DateTimeOffset.MinValue : IsoTimestamp.Parse(row.StartedAt))
            .Take(limit)
            .Select(row => MapRunToDomain(row, runsDirectory, taskId, ownerUserId!, includeContent: true))
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentTaskRun>>(runs);
    }

    /// <inheritdoc />
    public async Task SaveRunAsync(AgentTaskRun run, CancellationToken ct = default)
    {
        var taskEntry = catalog.GetTasksForUser(run.UserId)
            .FirstOrDefault(entry => string.Equals(entry.TaskId, run.TaskId, StringComparison.OrdinalIgnoreCase));
        if (taskEntry is null)
        {
            logger.LogWarning(
                "Discarding run '{RunId}' because task '{TaskId}' is no longer cataloged for its original owner '{UserId}'.",
                run.Id,
                run.TaskId,
                run.UserId);
            return;
        }

        var ownerUserId = taskEntry.OwnerUserId;
        var taskDirectory = taskEntry.DirectoryPath;
        var runDirectory = GetRunsDirectory(taskDirectory);
        if (!contentProtector.IsAvailable(ownerUserId))
        {
            throw new InvalidOperationException("Unlock your Vault Password before continuing.");
        }
        await indexCoordinator.RunForTaskAsync(ownerUserId, run.TaskId, async cancellationToken =>
        {
            await WriteRunMessagesAsync(runDirectory, run, ownerUserId, cancellationToken).ConfigureAwait(false);
            await WriteRunSessionAsync(runDirectory, run, ownerUserId, cancellationToken).ConfigureAwait(false);
            await UpsertRunIndexAsync(runDirectory, run, ownerUserId, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private AgentTaskDefinition? ReadExistingTask(string taskFilePath, string ownerUserId)
    {
        // A catalog snapshot can outlive a concurrently deleted task definition.
        try { return ReadFromDisk(taskFilePath, ownerUserId); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private AgentTaskDefinition ReadFromDisk(string taskFilePath, string ownerUserIdFromCatalog)
    {
        var text = EncryptedFileContent.ReadAllText(ownerUserIdFromCatalog, taskFilePath, contentProtector)
            ?? throw new FileNotFoundException("Task definition not found.", taskFilePath);
        return ParseDefinition(ownerUserIdFromCatalog, text);
    }

    /// <summary>Parses caller-owned task markdown for validation before a repository save.</summary>
    public AgentTaskDefinition ParseDefinition(string ownerUserIdFromCatalog, string fileText)
    {
        var (yaml, body) = MarkdownFrontmatter.Split(fileText);
        yamlCodec.RequireSchemaVersion(yaml, AgentTaskDefinitionFile.CurrentSchemaVersion);
        var file = yamlCodec.Deserialize<AgentTaskDefinitionFile>(yaml);

        return new AgentTaskDefinition
        {
            Id = file.Id,
            Name = file.Name,
            RequiresHandler = file.RequiresHandler,
            Instruction = body,
            InstructionSource = file.InstructionSource,
            InstructionMode = file.InstructionMode,
            AgentId = file.AgentId,
            UserId = ownerUserIdFromCatalog,
            ModelOverrideId = file.ModelOverrideId,
            Schedule = MapScheduleToDomain(file.Schedule),
            IsEnabled = file.IsEnabled,
            NotifyOnCompletion = file.NotifyOnCompletion,
            WaitForOwnerProviderIdle = file.WaitForOwnerProviderIdle,
            NotificationChannel = file.NotificationChannel,
            SessionMode = ParseEnum(file.SessionMode, TaskSessionMode.Isolated),
            MaxTokenBudget = file.MaxTokenBudget,
            ToolApprovalRunMode = ParseEnum(file.ToolApprovalRunMode, ToolApprovalRunMode.AutoRejectRequired),
            RunScope = MapRunScopeToDomain(file.RunScope),
            CreatedAt = string.IsNullOrWhiteSpace(file.CreatedAt) ? DateTimeOffset.UtcNow : IsoTimestamp.Parse(file.CreatedAt),
            CreatedByAgentId = file.CreatedByAgentId,
            ExternalTrigger = MapExternalTriggerToDomain(file.ExternalTrigger)
        };
    }

    private void WriteToDisk(string targetDirectory, AgentTaskDefinition task)
    {
        ValidateTask(task);
        Directory.CreateDirectory(targetDirectory);

        var file = new AgentTaskDefinitionFile
        {
            Id = task.Id,
            Name = task.Name,
            InstructionSource = task.InstructionSource,
            InstructionMode = task.InstructionMode,
            RequiresHandler = task.RequiresHandler,
            AgentId = task.AgentId,
            ModelOverrideId = task.ModelOverrideId,
            Schedule = MapScheduleToFile(task.Schedule),
            IsEnabled = task.IsEnabled,
            NotifyOnCompletion = task.NotifyOnCompletion,
            WaitForOwnerProviderIdle = task.WaitForOwnerProviderIdle,
            NotificationChannel = task.NotificationChannel,
            SessionMode = TaskFileValueFormatter.ToSnakeCase(task.SessionMode.ToString()),
            MaxTokenBudget = task.MaxTokenBudget,
            ToolApprovalRunMode = TaskFileValueFormatter.ToSnakeCase(task.ToolApprovalRunMode.ToString()),
            RunScope = MapRunScopeToFile(task.RunScope),
            CreatedAt = IsoTimestamp.FormatSeconds(task.CreatedAt),
            CreatedByAgentId = task.CreatedByAgentId,
            ExternalTrigger = ShouldPersistExternalTrigger(task.ExternalTrigger)
                ? MapExternalTriggerToFile(task.ExternalTrigger)
                : null
        };

        var yaml = yamlCodec.Serialize(file);
        var content = MarkdownFrontmatter.Combine(yaml, task.Instruction);
        var finalPath = GetTaskFilePath(targetDirectory);
        EncryptedFileContent.WriteAllText(task.UserId, finalPath, content, contentProtector);
    }

    private AgentTaskRun? ReadLatestRun(
        string taskDirectory,
        string containingTaskId,
        string ownerUserId,
        bool includeContent)
    {
        var runsDirectory = GetRunsDirectory(taskDirectory);
        var indexPath = GetRunsIndexPath(runsDirectory);
        if (!File.Exists(indexPath))
        {
            return null;
        }

        return ReadRunIndexRows(indexPath, containingTaskId, ownerUserId)
            .OrderByDescending(row => string.IsNullOrWhiteSpace(row.StartedAt) ? DateTimeOffset.MinValue : IsoTimestamp.Parse(row.StartedAt))
            .Select(row => MapRunToDomain(row, runsDirectory, containingTaskId, ownerUserId, includeContent))
            .FirstOrDefault();
    }

    private bool TryMapRunIndexLine(
        string line,
        string indexPath,
        int lineNumber,
        string containingTaskId,
        string ownerUserId,
        out AgentTaskRunIndexFile? run)
    {
        run = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var opened = contentProtector.Unprotect(ownerUserId, RunIndexPurpose(containingTaskId), line)
            ?? throw new InvalidOperationException("The encrypted task index cannot be read.");
        try
        {
            run = JsonSerializer.Deserialize<AgentTaskRunIndexFile>(opened, RunSerializerOptions)
                ?? throw new InvalidDataException("Task run index line could not be deserialized.");
            if (string.IsNullOrWhiteSpace(run.Id))
            {
                throw new InvalidDataException("Task run index line is missing an id.");
            }

            if (!PathHelper.IsSafePathSegment(run.Id))
            {
                throw new InvalidDataException("Task run index line contains an invalid id.");
            }

            if (!string.IsNullOrWhiteSpace(run.StartedAt))
            {
                _ = IsoTimestamp.Parse(run.StartedAt);
            }

            if (!string.IsNullOrWhiteSpace(run.CompletedAt))
            {
                _ = IsoTimestamp.Parse(run.CompletedAt);
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidDataException)
        {
            logger.LogWarning(
                exception,
                "Skipping malformed task run index line {LineNumber} in '{IndexPath}' for task '{TaskId}'.",
                lineNumber,
                indexPath,
                containingTaskId);
            return false;
        }
    }

    private List<AgentTaskRunIndexFile> ReadRunIndexRows(string indexPath, string containingTaskId, string ownerUserId)
    {
        if (!contentProtector.IsAvailable(ownerUserId)) { return []; }
        var rows = new List<AgentTaskRunIndexFile>();
        var seenRunIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = FileUtil.ReadAllLinesShared(indexPath);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            if (TryMapRunIndexLine(lines[index], indexPath, index + 1, containingTaskId, ownerUserId, out var row) &&
                row is not null &&
                seenRunIds.Add(row.Id))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private AgentTaskRun MapRunToDomain(
        AgentTaskRunIndexFile file, string runsDirectory, string containingTaskId,
        string ownerUserId, bool includeContent)
    {
        var taskId = file.TaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            taskId = containingTaskId;
            logger.LogWarning("Run '{RunId}' is missing task_id. Using containing task '{TaskId}'.", file.Id, containingTaskId);
        }
        return new AgentTaskRun
        {
            Id = file.Id,
            TaskId = taskId,
            UserId = ownerUserId,
            Status = ParseEnum(file.Status, AgentTaskStatus.Pending),
            StartedAt = string.IsNullOrWhiteSpace(file.StartedAt) ? null : IsoTimestamp.Parse(file.StartedAt),
            CompletedAt = string.IsNullOrWhiteSpace(file.CompletedAt) ? null : IsoTimestamp.Parse(file.CompletedAt),
            TokensUsed = file.TokensUsed,
            TriggerSource = ParseEnum(file.TriggerSource, TaskTriggerSource.Schedule),
            ResultSummary = includeContent ? file.ResultSummary : null,
            Error = includeContent ? file.Error : null,
            SystemInstruction = includeContent ? file.SystemInstruction : null,
            TriggerParameters = includeContent ? CloneParameters(file.TriggerParameters) : [],
            SessionElement = includeContent ? ReadRunSession(runsDirectory, file.Id, taskId, ownerUserId) : null,
            LiveMessages = includeContent ? ReadRunMessages(runsDirectory, file.Id, taskId, ownerUserId) : null
        };
    }



    private async Task WriteRunMessagesAsync(
        string runsDirectory,
        AgentTaskRun run,
        string ownerUserId,
        CancellationToken ct)
    {
        var messages = run.LiveMessages;
        if (redactionService is not null && messages is { Count: > 0 })
        {
            messages = [.. await redactionService.RedactMessagesAsync([.. messages], ownerUserId, ct).ConfigureAwait(false)];
        }

        var header = new AgentTaskRunTranscriptFile
        {
            Kind = "header",
            RunId = run.Id,
            TaskId = run.TaskId,
            SystemInstruction = run.SystemInstruction is null || redactionService is null
                ? run.SystemInstruction
                : await redactionService.RedactTextAsync(run.SystemInstruction, ownerUserId, ct).ConfigureAwait(false)
        };
        var lines = new List<string> { SerializeRunRecord(header, ownerUserId) };

        if (messages is not null)
        {
            lines.AddRange(messages.Select(message => SerializeRunRecord(new AgentTaskRunTranscriptFile
            {
                Kind = "message",
                RunId = run.Id,
                TaskId = run.TaskId,
                Message = message
            }, ownerUserId)));
        }

        await FileUtil.WriteAllTextAtomicAsync(GetRunTranscriptPath(runsDirectory, run.Id), string.Join(Environment.NewLine, lines) + Environment.NewLine, ct).ConfigureAwait(false);
    }

    private string SerializeRunRecord(AgentTaskRunTranscriptFile record, string ownerUserId)
    {
        return contentProtector.Protect(ownerUserId, RunPurpose(record.TaskId, record.RunId, "transcript"),
            JsonSerializer.Serialize(record, RunSerializerOptions));
    }

    private async Task WriteRunSessionAsync(
        string runsDirectory,
        AgentTaskRun run,
        string ownerUserId,
        CancellationToken ct)
    {
        var sessionPath = GetRunSessionPath(runsDirectory, run.Id);
        if (run.SessionElement is null)
        {
            File.Delete(sessionPath);
            return;
        }

        var raw = run.SessionElement.Value.GetRawText();
        var content = contentProtector.Protect(ownerUserId, RunPurpose(run.TaskId, run.Id, "session"), raw);
        await FileUtil.WriteAllTextAtomicAsync(sessionPath, content, ct).ConfigureAwait(false);
    }

    private async Task UpsertRunIndexAsync(
        string runsDirectory,
        AgentTaskRun run,
        string ownerUserId,
        CancellationToken ct)
    {
        var indexPath = GetRunsIndexPath(runsDirectory);
        var rows = File.Exists(indexPath) ? ReadRunIndexRows(indexPath, run.TaskId, ownerUserId) : [];
        rows.RemoveAll(row => string.Equals(row.Id, run.Id, StringComparison.OrdinalIgnoreCase));
        var systemInstruction = run.SystemInstruction is null || redactionService is null
            ? run.SystemInstruction
            : await redactionService.RedactTextAsync(run.SystemInstruction, ownerUserId, ct).ConfigureAwait(false);
        var indexRow = MapRunToIndexFile(run, systemInstruction);
        if (redactionService is not null)
        {
            if (indexRow.ResultSummary is not null)
            {
                indexRow.ResultSummary = await redactionService.RedactTextAsync(indexRow.ResultSummary, ownerUserId, ct).ConfigureAwait(false);
            }

            if (indexRow.Error is not null)
            {
                indexRow.Error = await redactionService.RedactTextAsync(indexRow.Error, ownerUserId, ct).ConfigureAwait(false);
            }

            if (indexRow.TriggerParameters is { Count: > 0 })
            {
                var redactedParameters = await redactionService.RedactArgumentsAsync(
                    indexRow.TriggerParameters.ToDictionary(
                        pair => pair.Key,
                        pair => (object?)pair.Value,
                        StringComparer.OrdinalIgnoreCase),
                    ownerUserId,
                    ct).ConfigureAwait(false);
                indexRow.TriggerParameters = redactedParameters.ToDictionary(
                    pair => pair.Key,
                    pair => JsonSerializer.SerializeToElement(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        rows.Add(indexRow);

        var content = string.Join(Environment.NewLine, rows.Select(row => contentProtector.Protect(ownerUserId, RunIndexPurpose(run.TaskId), JsonSerializer.Serialize(row, RunSerializerOptions)))) + Environment.NewLine;
        await FileUtil.WriteAllTextAtomicAsync(indexPath, content, ct).ConfigureAwait(false);
    }

    private static AgentTaskRunIndexFile MapRunToIndexFile(AgentTaskRun run, string? systemInstruction)
    {
        return new AgentTaskRunIndexFile
        {
            Id = run.Id,
            TaskId = run.TaskId,
            Status = TaskFileValueFormatter.ToSnakeCase(run.Status.ToString()),
            StartedAt = run.StartedAt.HasValue ? IsoTimestamp.FormatMilliseconds(run.StartedAt.Value) : null,
            CompletedAt = run.CompletedAt.HasValue ? IsoTimestamp.FormatMilliseconds(run.CompletedAt.Value) : null,
            ResultSummary = run.ResultSummary,
            Error = run.Error,
            TokensUsed = run.TokensUsed,
            TriggerSource = TaskFileValueFormatter.ToSnakeCase(run.TriggerSource.ToString()),
            TriggerParameters = run.TriggerParameters.Count == 0
                ? null
                : CloneParameters(run.TriggerParameters),
            SystemInstruction = systemInstruction
        };
    }

    private List<ChatMessage>? ReadRunMessages(
        string runsDirectory,
        string runId,
        string taskId,
        string ownerUserId)
    {
        var transcriptPath = GetRunTranscriptPath(runsDirectory, runId);
        if (!File.Exists(transcriptPath))
        {
            return null;
        }

        var messages = new List<ChatMessage>();
        var lines = FileUtil.ReadAllLinesShared(transcriptPath);
        for (var index = 0; index < lines.Length; index++)
        {
            try
            {
                var record = JsonSerializer.Deserialize<AgentTaskRunTranscriptFile>(
                    contentProtector.Unprotect(ownerUserId, RunPurpose(taskId, runId, "transcript"), lines[index])
                        ?? throw new InvalidOperationException("The encrypted task transcript cannot be read."), RunSerializerOptions);
                if (record is null)
                {
                    continue;
                }

                if (record is { Message: not null } &&
                    string.Equals(record.Kind, "message", StringComparison.OrdinalIgnoreCase))
                {
                    messages.Add(record.Message);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping malformed task run transcript line {LineNumber} in '{TranscriptPath}' for task '{TaskId}'.", index + 1, transcriptPath, taskId);
            }
        }

        return messages.Count == 0 ? null : messages;
    }



    private JsonElement? ReadRunSession(string runsDirectory, string runId, string taskId, string ownerUserId)
    {
        var sessionPath = GetRunSessionPath(runsDirectory, runId);
        if (!File.Exists(sessionPath)) { return null; }
        var opened = contentProtector.Unprotect(ownerUserId, RunPurpose(taskId, runId, "session"), File.ReadAllText(sessionPath))
            ?? throw new InvalidOperationException("The encrypted task session cannot be read.");
        try
        {
            using var document = JsonDocument.Parse(opened);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to read task run session file '{SessionPath}'.", sessionPath);
            return null;
        }
    }

    private static TaskExternalTrigger MapExternalTriggerToDomain(ExternalTriggerFile? file)
    {
        if (file is null)
        {
            return new TaskExternalTrigger();
        }

        return new TaskExternalTrigger
        {
            IsEnabled = file.IsEnabled,
            Slug = string.IsNullOrWhiteSpace(file.Slug) ? string.Empty : Slugs.ForTaskTrigger(file.Slug),
            Parameters = [.. file.Parameters.Select(parameter => new TaskExternalParameter
            {
                Name = parameter.Name?.Trim() ?? string.Empty,
                Kind = ParseEnum(parameter.Kind, TaskExternalParameterKind.String),
                IsRequired = parameter.IsRequired
            })]
        };
    }

    private static ExternalTriggerFile MapExternalTriggerToFile(TaskExternalTrigger trigger)
    {
        return new ExternalTriggerFile
        {
            IsEnabled = trigger.IsEnabled,
            Slug = trigger.Slug,
            Parameters = [.. trigger.Parameters.Select(parameter => new ExternalTriggerParameterFile
            {
                Name = parameter.Name,
                Kind = TaskFileValueFormatter.ToSnakeCase(parameter.Kind.ToString()),
                IsRequired = parameter.IsRequired
            })]
        };
    }

    private static bool ShouldPersistExternalTrigger(TaskExternalTrigger trigger)
    {
        return trigger.IsEnabled || !string.IsNullOrWhiteSpace(trigger.Slug) || trigger.Parameters.Count > 0;
    }

    private static TaskRunScope? MapRunScopeToDomain(TaskRunScopeFile? file)
    {
        if (file is null)
        {
            return null;
        }

        return new TaskRunScope
        {
            AllowedToolNames = [.. file.AllowedToolNames],
            ToolPolicies = [.. file.ToolPolicies
                .Where(policy => !string.IsNullOrWhiteSpace(policy.ToolName))
                .Select(policy => new AgentToolPolicy
                {
                    ToolName = policy.ToolName,
                    ApprovalMode = policy.ApprovalMode
                })],
            IsToolSearchEnabled = file.IsToolSearchEnabled,
            PinnedToolNames = [.. file.PinnedToolNames],
            IsCompactionEnabled = file.IsCompactionEnabled,
            IsMemoryEnabled = file.IsMemoryEnabled,
            IsTodoEnabled = file.IsTodoEnabled,
            IsLocalOnly = file.IsLocalOnly,
            AgentToolIds = [.. file.AgentToolIds],
            AllowedSkillNames = [.. file.AllowedSkillNames],
            AllowedWorkspaces = [.. file.AllowedWorkspaces],
            AllowedDirectories = [.. file.AllowedDirectories.Select(ToAllowedDirectory)]
        };
    }

    private static TaskRunScopeFile? MapRunScopeToFile(TaskRunScope? scope)
    {
        if (scope is null)
        {
            return null;
        }

        return new TaskRunScopeFile
        {
            AllowedToolNames = [.. scope.AllowedToolNames],
            ToolPolicies = [.. scope.ToolPolicies
                .Where(ToolPolicyResolver.IsPersistedOverride)
                .Select(policy => new AgentToolPolicyFile
                {
                    ToolName = policy.ToolName,
                    ApprovalMode = policy.ApprovalMode
                })],
            IsToolSearchEnabled = scope.IsToolSearchEnabled,
            PinnedToolNames = [.. scope.PinnedToolNames],
            IsCompactionEnabled = scope.IsCompactionEnabled,
            IsMemoryEnabled = scope.IsMemoryEnabled,
            IsTodoEnabled = scope.IsTodoEnabled,
            IsLocalOnly = scope.IsLocalOnly,
            AgentToolIds = [.. scope.AgentToolIds],
            AllowedSkillNames = [.. scope.AllowedSkillNames],
            AllowedWorkspaces = [.. scope.AllowedWorkspaces],
            AllowedDirectories = [.. scope.AllowedDirectories.Select(ToAllowedDirectoryFile)]
        };
    }

    private static AllowedDirectory ToAllowedDirectory(AllowedDirectoryFile file)
    {
        var kind = ParseEnum(file.Kind, DirectorySourceKind.LocalPath);
        var accessMode = ParseEnum(file.AccessMode, WorkspaceAccessMode.ReadOnly);
        return new AllowedDirectory(file.Value, file.Label, kind, accessMode);
    }

    private static AllowedDirectoryFile ToAllowedDirectoryFile(AllowedDirectory directory)
    {
        return new AllowedDirectoryFile
        {
            Value = directory.Value,
            Label = directory.Label,
            Kind = directory.Kind.ToString(),
            AccessMode = directory.AccessMode.ToString()
        };
    }

    /// <inheritdoc />
    public void ValidateTask(AgentTaskDefinition task)
    {
        task.ExternalTrigger ??= new TaskExternalTrigger();
        var trigger = task.ExternalTrigger;

        var requestedSlug = trigger.Slug?.Trim() ?? string.Empty;
        var normalizedSlug = string.IsNullOrWhiteSpace(requestedSlug)
            ? string.Empty
            : Slugs.ForUserTaskTrigger(task.UserId, requestedSlug);

        trigger.Parameters = NormalizeExternalParametersOrThrow(trigger.Parameters);

        if (!trigger.IsEnabled)
        {
            trigger.Slug = normalizedSlug;
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedSlug))
        {
            normalizedSlug = Slugs.ForUserTaskTrigger(task.UserId, task.Name);
        }

        var existingSlugs = GetExistingExternalTriggerSlugs(task.UserId, task.Id);
        trigger.Slug = string.IsNullOrWhiteSpace(requestedSlug)
            ? Slugs.MakeUnique(normalizedSlug, existingSlugs)
            : normalizedSlug;

        if (existingSlugs.Contains(trigger.Slug))
        {
            throw new DuplicateExternalTriggerSlugException(trigger.Slug);
        }
    }

    private static List<TaskExternalParameter> NormalizeExternalParametersOrThrow(IEnumerable<TaskExternalParameter> parameters)
    {
        var normalized = new List<TaskExternalParameter>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            var name = parameter.Name?.Trim() ?? string.Empty;
            if (!IsValidExternalParameterName(name))
            {
                throw new InvalidOperationException($"External trigger parameter '{name}' has an invalid name.");
            }

            if (!names.Add(name))
            {
                throw new InvalidOperationException($"External trigger parameter '{name}' is duplicated.");
            }

            if (!Enum.IsDefined(parameter.Kind))
            {
                throw new InvalidOperationException($"External trigger parameter '{name}' has an unsupported kind.");
            }

            normalized.Add(new TaskExternalParameter
            {
                Name = name,
                Kind = parameter.Kind,
                IsRequired = parameter.IsRequired
            });
        }

        return normalized;
    }

    private static bool IsValidExternalParameterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private HashSet<string> GetExistingExternalTriggerSlugs(string ownerUserId, string taskIdToExclude)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasksRoot = GetTasksRoot(ownerUserId);
        if (!Directory.Exists(tasksRoot)) { return slugs; }
        foreach (var directory in Directory.EnumerateDirectories(tasksRoot))
        {
            var taskFilePath = GetTaskFilePath(directory);
            if (!File.Exists(taskFilePath)) { continue; }
            try
            {
                var text = EncryptedFileContent.ReadAllText(ownerUserId, taskFilePath, contentProtector)
                    ?? throw new FileNotFoundException("Task definition not found.", taskFilePath);
                var (yaml, _) = MarkdownFrontmatter.Split(text);
                if (string.IsNullOrWhiteSpace(yaml)) { continue; }
                yamlCodec.RequireSchemaVersion(yaml, AgentTaskDefinitionFile.CurrentSchemaVersion);
                var file = yamlCodec.Deserialize<AgentTaskDefinitionFile>(yaml);
                if (string.Equals(file.Id, taskIdToExclude, StringComparison.OrdinalIgnoreCase) ||
                    file.ExternalTrigger is not { IsEnabled: true } trigger ||
                    string.IsNullOrWhiteSpace(trigger.Slug))
                {
                    continue;
                }
                slugs.Add(Slugs.ForUserTaskTrigger(ownerUserId, trigger.Slug));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or JsonException)
            {
                logger.LogWarning(ex, "Skipping task file '{TaskFilePath}' while checking external trigger slugs.", taskFilePath);
            }
        }
        return slugs;
    }

    private IEnumerable<string> EnumerateTaskFiles()
    {
        var usersRoot = _usersRootResolver();
        if (!Directory.Exists(usersRoot))
        {
            yield break;
        }

        foreach (var userDirectory in Directory.EnumerateDirectories(usersRoot))
        {
            var tasksRoot = Path.Combine(userDirectory, AgentTaskFileLayout.TasksDirectory);
            if (!Directory.Exists(tasksRoot))
            {
                continue;
            }

            foreach (var taskDirectory in Directory.EnumerateDirectories(tasksRoot))
            {
                var taskFilePath = GetTaskFilePath(taskDirectory);
                if (File.Exists(taskFilePath))
                {
                    yield return taskFilePath;
                }
            }
        }
    }

    private static string? GetOwnerUserIdFromTaskFilePath(string taskFilePath)
    {
        var taskDirectory = Path.GetDirectoryName(taskFilePath);
        var tasksDirectory = taskDirectory is null ? null : Directory.GetParent(taskDirectory);
        var userDirectory = tasksDirectory?.Parent;
        return userDirectory?.Name;
    }

    private static Dictionary<string, JsonElement> CloneParameters(IDictionary<string, JsonElement>? parameters)
    {
        return parameters?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase) ?? [];
    }

    private static TaskSchedule MapScheduleToDomain(ScheduleFile file)
    {
        return new TaskSchedule
        {
            Kind = ParseScheduleKind(file.Kind),
            IntervalMinutes = file.IntervalMinutes,
            RunAt = string.IsNullOrWhiteSpace(file.RunAt) ? null : IsoTimestamp.Parse(file.RunAt),
            CronExpression = file.Cron,
            TimeZoneId = file.Timezone,
            DeleteAfterRun = file.DeleteAfterRun,
            PredecessorTaskId = string.IsNullOrWhiteSpace(file.PredecessorTaskId) ? null : file.PredecessorTaskId.Trim()
        };
    }

    private static ScheduleFile MapScheduleToFile(TaskSchedule schedule)
    {
        return new ScheduleFile
        {
            Kind = TaskFileValueFormatter.ToSnakeCase(schedule.Kind.ToString()),
            IntervalMinutes = schedule.Kind == TaskScheduleKind.Interval ? schedule.IntervalMinutes : null,
            RunAt = schedule.Kind == TaskScheduleKind.OneShot && schedule.RunAt.HasValue
                ? IsoTimestamp.FormatSeconds(schedule.RunAt.Value)
                : null,
            Cron = schedule.Kind == TaskScheduleKind.Cron ? schedule.CronExpression : null,
            Timezone = schedule.Kind is TaskScheduleKind.OneShot or TaskScheduleKind.Cron ? schedule.TimeZoneId : null,
            DeleteAfterRun = schedule.DeleteAfterRun,
            PredecessorTaskId = schedule.Kind == TaskScheduleKind.AfterTask ? schedule.PredecessorTaskId?.Trim() : null
        };
    }

    private static TaskScheduleKind ParseScheduleKind(string value)
    {
        return NormalizeToken(value) switch
        {
            "interval" => TaskScheduleKind.Interval,
            "one_shot" => TaskScheduleKind.OneShot,
            "cron" => TaskScheduleKind.Cron,
            "after_task" => TaskScheduleKind.AfterTask,
            _ => TaskScheduleKind.Interval
        };
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = NormalizeToken(value);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(TaskFileValueFormatter.ToSnakeCase(name), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        return fallback;
    }

    private static string NormalizeToken(string value)
    {
        return value.Trim().Replace('-', '_').ToLowerInvariant();
    }

    private HashSet<string> GetExistingSlugs(string tasksRoot)
    {
        var slugs = new HashSet<string>(_reservedTaskSlugs, StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(tasksRoot))
        {
            return slugs;
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(tasksRoot))
        {
            var slug = Path.GetFileName(directoryPath);
            if (!string.IsNullOrWhiteSpace(slug))
            {
                slugs.Add(slug);
            }
        }

        return slugs;
    }

    private string GetTasksRoot(string userId)
    {
        if (!PathHelper.IsSafePathSegment(userId))
        {
            throw new ArgumentException($"Invalid user id '{userId}'.", nameof(userId));
        }

        if (!PathHelper.TryResolveContainedPath(
                _usersRootResolver(),
                Path.Combine(userId, AgentTaskFileLayout.TasksDirectory),
                out var tasksRoot))
        {
            throw new UnauthorizedAccessException($"Task path escapes the configured users root: {userId}");
        }

        return tasksRoot;
    }

    private static string GetTaskFilePath(string taskDirectory)
    {
        return Path.Combine(taskDirectory, AgentTaskFileLayout.TaskFile);
    }

    private static string GetRunsDirectory(string taskDirectory)
    {
        return Path.Combine(taskDirectory, AgentTaskFileLayout.RunsDirectory);
    }

    private static string GetRunsIndexPath(string runsDirectory)
    {
        return Path.Combine(runsDirectory, AgentTaskFileLayout.RunIndexFile);
    }

    private static string GetRunTranscriptPath(string runsDirectory, string runId)
    {
        return GetRunArtifactPath(runsDirectory, runId, AgentTaskFileLayout.TranscriptExtension);
    }

    private static string GetRunSessionPath(string runsDirectory, string runId)
    {
        return GetRunArtifactPath(runsDirectory, runId, AgentTaskFileLayout.SessionFileSuffix);
    }

    private static string GetRunArtifactPath(string runsDirectory, string runId, string suffix)
    {
        if (!PathHelper.IsSafePathSegment(runId))
        {
            throw new ArgumentException($"Invalid run id '{runId}'.", nameof(runId));
        }

        if (!PathHelper.TryResolveContainedPath(runsDirectory, runId + suffix, out var path))
        {
            throw new UnauthorizedAccessException($"Run artifact path escapes its task directory: {runId}");
        }

        return path;
    }

    private static bool IsOwnedBy(string? ownerUserId, string userId)
    {
        return string.Equals(ownerUserId, userId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Retries task deletion when a just-finished run is still releasing atomic-write temp files.
    /// </summary>
    private static async Task DeleteTaskDirectoryWithRetryAsync(string taskDirectory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(taskDirectory))
                {
                    Directory.Delete(taskDirectory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (IsTransientDeleteFailure(ex) && attempt < 4)
            {
                if (!Directory.Exists(taskDirectory))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientDeleteFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }
}
