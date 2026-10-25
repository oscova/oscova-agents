using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Persistence;
using Oscova.Agents.Utils;
using YamlDotNet.Core;

namespace Oscova.Agents.Tasks;

/// <summary>Builds an in-memory catalogue from caller-owned task directories.</summary>
public sealed class AgentTaskCatalog : IAgentTaskCatalog
{
    private readonly Func<string> _usersRootResolver;
    private readonly YamlConverter _yamlCodec;
    private readonly ILogger<AgentTaskCatalog> _logger;
    private readonly Func<string, bool> _userIdValidator;
    private readonly Func<AgentTaskDefinitionFile, string?>? _persistedDefinitionValidator;
    private readonly Lock _writeLock = new();

    private AgentTaskCatalogState _state = AgentTaskCatalogState.Empty;

    private readonly Oscova.Agents.Secrets.IContentProtector _protector;

    /// <summary>Initializes a task catalogue with caller-owned storage and classification policies.</summary>
    public AgentTaskCatalog(
        Func<string> usersRootResolver,
        YamlConverter yamlCodec,
        ILogger<AgentTaskCatalog> logger,
        Oscova.Agents.Secrets.IContentProtector protector,
        Func<string, bool>? userIdValidator = null,
        Func<AgentTaskDefinitionFile, string?>? persistedDefinitionValidator = null)
    {
        _usersRootResolver = usersRootResolver ?? throw new ArgumentNullException(nameof(usersRootResolver));
        _yamlCodec = yamlCodec;
        _logger = logger;
        _protector = protector;
        _userIdValidator = userIdValidator ?? (static _ => true);
        _persistedDefinitionValidator = persistedDefinitionValidator;
    }

    /// <inheritdoc />
    public string? TryGetTaskDirectory(string taskId)
    {
        var state = _state;
        return state.EntriesByTaskId.TryGetValue(taskId, out var entry) ? entry.DirectoryPath : null;
    }

    /// <inheritdoc />
    public string? TryGetOwnerUserId(string taskId)
    {
        var state = _state;
        return state.EntriesByTaskId.TryGetValue(taskId, out var entry) ? entry.OwnerUserId : null;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AgentTaskCatalogEntry> GetTasksForUser(string userId)
    {
        var state = _state;
        return state.EntriesByOwnerUserId.TryGetValue(userId, out var entries) ? entries.AsReadOnly() : [];
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AgentTaskCatalogEntry> GetAllEnabledTasks()
    {
        return _state.EnabledEntries.AsReadOnly();
    }

    /// <inheritdoc />
    public Task ReloadAsync(CancellationToken ct = default)
    {
        var fresh = AgentTaskCatalogState.Empty;
        var usersRoot = Path.GetFullPath(_usersRootResolver());

        if (Directory.Exists(usersRoot))
        {
            foreach (var userDirectoryPath in Directory.EnumerateDirectories(usersRoot))
            {
                ct.ThrowIfCancellationRequested();

                var ownerUserId = Path.GetFileName(userDirectoryPath);
                if (!PathHelper.IsSafePathSegment(ownerUserId) || !_userIdValidator(ownerUserId))
                {
                    _logger.LogWarning(
                        "Skipping task catalogue user directory '{UserDirectoryPath}' because its user id was rejected by caller policy.",
                        userDirectoryPath);
                    continue;
                }

                if (PathHelper.HasSymlinkInPath(Path.GetFullPath(userDirectoryPath), usersRoot))
                {
                    _logger.LogWarning(
                        "Skipping task catalogue user directory '{UserDirectoryPath}' because its path contains a symbolic link or reparse point.",
                        userDirectoryPath);
                    continue;
                }

                fresh = MergeUserSlice(fresh, ownerUserId, ScanUserTasks(userDirectoryPath, ownerUserId, ct));
            }
        }

        lock (_writeLock)
        {
            _state = fresh;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReloadUserAsync(string userId, CancellationToken ct = default)
    {
        if (!PathHelper.IsSafePathSegment(userId) || !_userIdValidator(userId))
        {
            return Task.CompletedTask;
        }

        var usersRoot = Path.GetFullPath(_usersRootResolver());
        var userDirectoryPath = Path.Combine(usersRoot, userId);
        var freshUserEntries = PathHelper.HasSymlinkInPath(userDirectoryPath, usersRoot)
            ? []
            : ScanUserTasks(userDirectoryPath, userId, ct);

        lock (_writeLock)
        {
            _state = MergeUserSlice(_state, userId, freshUserEntries);
        }
        return Task.CompletedTask;
    }

    // Returns a new state with userId's slice replaced. Pure: never mutates the input state.
    private AgentTaskCatalogState MergeUserSlice(
        AgentTaskCatalogState current,
        string userId,
        List<AgentTaskCatalogEntry> freshUserEntries)
    {
        var entriesByTaskId = new Dictionary<string, AgentTaskCatalogEntry>(current.EntriesByTaskId, StringComparer.OrdinalIgnoreCase);
        var entriesByOwnerUserId = new Dictionary<string, List<AgentTaskCatalogEntry>>(current.EntriesByOwnerUserId, StringComparer.OrdinalIgnoreCase);

        if (entriesByOwnerUserId.TryGetValue(userId, out var oldOwnerEntries))
        {
            foreach (var oldEntry in oldOwnerEntries)
            {
                entriesByTaskId.Remove(oldEntry.TaskId);
            }
            entriesByOwnerUserId.Remove(userId);
        }

        var newOwnerEntries = new List<AgentTaskCatalogEntry>();
        foreach (var entry in freshUserEntries)
        {
            if (entriesByTaskId.TryGetValue(entry.TaskId, out var existingEntry))
            {
                _logger.LogWarning(
                    "Skipping duplicate task id '{TaskId}' at '{TaskDirectoryPath}' for user '{OwnerUserId}'. Keeping the task at '{ExistingTaskDirectoryPath}' for user '{ExistingOwnerUserId}'.",
                    entry.TaskId,
                    entry.DirectoryPath,
                    entry.OwnerUserId,
                    existingEntry.DirectoryPath,
                    existingEntry.OwnerUserId);
                continue;
            }

            entriesByTaskId.Add(entry.TaskId, entry);
            newOwnerEntries.Add(entry);
        }

        if (newOwnerEntries.Count > 0)
        {
            entriesByOwnerUserId[userId] = newOwnerEntries;
        }

        var enabledEntries = entriesByTaskId.Values.Where(entry => entry.IsEnabled).ToList();
        return new AgentTaskCatalogState(entriesByTaskId, entriesByOwnerUserId, enabledEntries);
    }

    private List<AgentTaskCatalogEntry> ScanUserTasks(
        string userDirectoryPath,
        string ownerUserId,
        CancellationToken ct)
    {
        var entries = new List<AgentTaskCatalogEntry>();
        if (!_protector.IsAvailable(ownerUserId)) { return entries; }
        var normalizedUserDirectory = Path.GetFullPath(userDirectoryPath);
        var tasksRoot = Path.Combine(normalizedUserDirectory, AgentTaskFileLayout.TasksDirectory);
        if (!Directory.Exists(tasksRoot) ||
            PathHelper.HasSymlinkInPath(tasksRoot, normalizedUserDirectory))
        {
            return entries;
        }

        var seenTaskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var taskDirectoryPath in Directory.EnumerateDirectories(tasksRoot))
        {
            ct.ThrowIfCancellationRequested();

            if (PathHelper.HasSymlinkInPath(Path.GetFullPath(taskDirectoryPath), tasksRoot))
            {
                _logger.LogWarning(
                    "Skipping task directory '{TaskDirectoryPath}' because its path contains a symbolic link or reparse point.",
                    taskDirectoryPath);
                continue;
            }

            var taskFilePath = Path.Combine(taskDirectoryPath, AgentTaskFileLayout.TaskFile);
            if (!File.Exists(taskFilePath))
            {
                continue;
            }

            try
            {
                var fileText = Secrets.EncryptedFileContent.ReadAllText(ownerUserId, taskFilePath, _protector)
                    ?? throw new FileNotFoundException("Task definition not found.", taskFilePath);
                var (yaml, _) = MarkdownFrontmatter.Split(fileText);
                _yamlCodec.RequireSchemaVersion(yaml, AgentTaskDefinitionFile.CurrentSchemaVersion);
                var file = _yamlCodec.Deserialize<AgentTaskDefinitionFile>(yaml);
                var taskId = file.Id;
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    continue;
                }

                var validationError = _persistedDefinitionValidator?.Invoke(file);
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    _logger.LogWarning(
                        "Task '{TaskId}' at '{TaskFilePath}' has an invalid persisted schedule: {Reason}",
                        taskId,
                        taskFilePath,
                        validationError);
                }

                if (!seenTaskIds.Add(taskId))
                {
                    _logger.LogWarning(
                        "Duplicate task id '{TaskId}' within user '{OwnerUserId}' at '{TaskDirectoryPath}'. Keeping the first catalogue entry.",
                        taskId,
                        ownerUserId,
                        taskDirectoryPath);
                    continue;
                }

                entries.Add(new AgentTaskCatalogEntry(
                    taskId,
                    ownerUserId,
                    taskDirectoryPath,
                    file.IsEnabled));
            }
            catch (Exception exception) when (exception is not InvalidDataException || exception.InnerException is YamlException)
            {
                _logger.LogWarning(
                    exception,
                    "Skipping task file '{TaskFilePath}' because it could not be loaded.",
                    taskFilePath);
            }
        }

        return entries;
    }

    private sealed record AgentTaskCatalogState(
        Dictionary<string, AgentTaskCatalogEntry> EntriesByTaskId,
        Dictionary<string, List<AgentTaskCatalogEntry>> EntriesByOwnerUserId,
        List<AgentTaskCatalogEntry> EnabledEntries)
    {
        public static AgentTaskCatalogState Empty { get; } = new(
            new Dictionary<string, AgentTaskCatalogEntry>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<AgentTaskCatalogEntry>>(StringComparer.OrdinalIgnoreCase),
            []);
    }
}
