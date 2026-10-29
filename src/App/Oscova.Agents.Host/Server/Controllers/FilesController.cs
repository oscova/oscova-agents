using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Files;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Secrets;
using Oscova.Agents.Utils;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Memory;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/files")]
[IgnoreAntiforgeryToken]
public sealed class FilesController(
    UserServiceManager userServiceManager,
    IUserDirectoryService userDirectoryService,
    IUserFileBackupService userFileBackupService,
    IContentProtector protector,
    Oscova.Agents.Infrastructure.Agents.AgentDefinitionRepository agentFiles,
    IAgentCatalog agentCatalog,
    Oscova.Agents.Tasks.IAgentTaskCatalog taskCatalog,
    Oscova.Agents.Settings.IAppSettingsRepository settingsRepository,
    SkillGovernanceService skillGovernance,
    IUserPermissionService permissions,
    OperatorVaultAccess vault,
    ILogger<FilesController> logger)
    : UserScopeControllerBase(userServiceManager), IAsyncActionFilter
{
    /// <summary>Routes file-editor mutations through the same policy as the owning settings and skills UI.</summary>
    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // The shared root needs the shared folder permission on every method. Home's settings and skill guards cover personal stores only.
        if (context.ActionArguments.TryGetValue("root", out var root) && root is FileRoot.Shared)
        {
            try
            {
                await permissions.RequireAsync(GetUserId(), UserPermissionKeys.SharedFolder, context.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (TryCreateProblem(ex, out var result))
            {
                context.Result = result;
                return;
            }

            await next();
            return;
        }

        if (HttpMethods.IsGet(Request.Method)) { await next(); return; }

        var values = context.ActionArguments.Values;
        var path = values.OfType<CreateDirectoryRequest>().Select(request => CombineRelativePath(request.Path, request.Name))
            .Concat(values.OfType<CreateFileRequest>().Select(request => CombineRelativePath(request.Path, request.Name)))
            .Concat(values.OfType<FileUploadRequest>().Where(request => request.File is not null)
                .Select(request => CombineRelativePath(request.Path, request.File!.FileName)))
            .Concat(values.OfType<WriteFileTextRequest>().Select(request => request.Path)).FirstOrDefault()
            ?? (context.ActionArguments.TryGetValue("path", out var value) ? value as string : null);
        if (path is not null)
        {
            try
            {
                var userId = GetUserId();
                var normalized = CanonicalFilePath(userId, path);
                var settingsPrefix = UserFileNames.SettingsDirectory + "/";
                if (normalized.StartsWith(settingsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var file = normalized[settingsPrefix.Length..];
                    if (new[] { ModelProviderSettings.Key, McpToolSettings.Key, SharedSkillSettings.Key }
                        .Any(key => string.Equals(file, key + ".yaml.enc", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new UnauthorizedAccessException("Edit this configuration through Settings so its permissions and active work are checked.");
                    }
                }

                await skillGovernance.RequirePathEditableAsync(userId,
                    Path.Combine(userDirectoryService.GetUserDirectoryPath(userId), normalized), context.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (TryCreateProblem(ex, out var result))
            {
                context.Result = result;
                return;
            }
        }

        await next();
    }

    private static readonly HashSet<string> RuntimePromptPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        PromptFileNames.BrowserUsePrompt,
        PromptFileNames.GoalJudgePrompt,
        PromptFileNames.GoalContinuationPrompt,
        PromptFileNames.CompactionPrompt,
        PromptFileNames.MemorySystemPrompt,
        PromptFileNames.EnvironmentPrompt,
        PromptFileNames.LibraryPrompt,
        PromptFileNames.ToolSearchPrompt,
        PromptFileNames.TodoProviderPrompt,
        PromptFileNames.SubagentsPrompt,
        PromptFileNames.SkillsPrompt,
        PromptFileNames.AvatarMarkupPrompt,
        PromptFileNames.ChatContinuationPrompt,
        PromptFileNames.ChatTitlePrompt,
        PromptFileNames.ToolFailureRecoveryPrompt
    };

    [HttpGet]
    public async Task<IActionResult> ListDirectory([FromQuery] string? path, CancellationToken ct, [FromQuery] FileRoot root = FileRoot.Home)
    {
        var userScope = await GetUserScopeAsync(ct);

        try
        {
            var entries = await userDirectoryService.ListDirectoryAsync(userScope.UserId, path ?? string.Empty, root);
            return Ok(entries);
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    [HttpPost("directories")]
    public async Task<IActionResult> CreateDirectory([FromBody] CreateDirectoryRequest request, CancellationToken ct,
        [FromQuery] FileRoot root = FileRoot.Home)
    {
        var userScope = await GetUserScopeAsync(ct);

        try
        {
            await userDirectoryService.CreateDirectoryAsync(userScope.UserId, request.Path ?? string.Empty, request.Name, root);
            return NoContent();
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    [HttpPost("documents")]
    public async Task<IActionResult> CreateFile([FromBody] CreateFileRequest request, CancellationToken ct,
        [FromQuery] FileRoot root = FileRoot.Home)
    {
        var userScope = await GetUserScopeAsync(ct);

        try
        {
            FileNameValidator.ThrowIfInvalid(request.Name);
            if (root == FileRoot.Shared)
            {
                await userDirectoryService.CreateFileAsync(userScope.UserId, request.Path ?? string.Empty, request.Name, request.Content, root);
                return NoContent();
            }

            var normalized = CanonicalFilePath(userScope.UserId, CombineRelativePath(request.Path, request.Name));
            if (GetKnowledgeStore(userScope.UserId, normalized) is not null || IsRuntimePromptPath(normalized) ||
                !string.IsNullOrEmpty(request.Content) && UserFileNames.IsEncryptedConfigurationPath(normalized))
            {
                if (System.IO.File.Exists(Path.Combine(userDirectoryService.GetUserDirectoryPath(userScope.UserId), normalized)))
                {
                    throw new IOException("The file already exists.");
                }

                return await SaveFileEditorTextAsync(userScope, new WriteFileTextRequest
                {
                    Path = normalized,
                    Content = request.Content ?? string.Empty,
                    Encrypt = true
                }, ct);
            }

            await userDirectoryService.CreateFileAsync(userScope.UserId, request.Path ?? string.Empty, request.Name, request.Content);
            var relativePath = CombineRelativePath(request.Path, request.Name);
            await HandleFileMutationAsync(userScope, relativePath);

            return NoContent();
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    [HttpDelete]
    public async Task<IActionResult> Delete([FromQuery] string path, [FromQuery] bool isDirectory, CancellationToken ct,
        [FromQuery] FileRoot root = FileRoot.Home)
    {
        var userScope = await GetUserScopeAsync(ct);

        try
        {
            if (isDirectory)
            {
                await userDirectoryService.DeleteDirectoryAsync(userScope.UserId, path, root);
            }
            else
            {
                await userDirectoryService.DeleteFileAsync(userScope.UserId, path, root);
            }

            if (root == FileRoot.Home)
            {
                await HandleFileMutationAsync(userScope, path, isDirectory);
            }

            return NoContent();
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    [HttpPost("upload")]
    [RequestSizeLimit(UserFileLimits.MaxRequestSizeBytes)]
    [RequestFormLimits(MemoryBufferThreshold = (int)UserFileLimits.MaxRequestSizeBytes)]
    public async Task<IActionResult> Upload([FromForm] FileUploadRequest request, CancellationToken ct,
        [FromQuery] FileRoot root = FileRoot.Home)
    {
        var userScope = await GetUserScopeAsync(ct);
        if (request.File is null || request.File.Length == 0)
        {
            return BadRequest("A file is required.");
        }

        try
        {
            FileNameValidator.ThrowIfInvalid(request.File.FileName);
            if (root == FileRoot.Shared)
            {
                await using var sharedStream = request.File.OpenReadStream();
                await userDirectoryService.UploadFileAsync(userScope.UserId, request.Path ?? string.Empty, request.File.FileName, sharedStream, root);
                return NoContent();
            }

            var normalized = CanonicalFilePath(userScope.UserId, CombineRelativePath(request.Path, request.File.FileName));
            if (GetKnowledgeStore(userScope.UserId, normalized) is not null || UserFileNames.IsEncryptedConfigurationPath(normalized))
            {
                await using var input = request.File.OpenReadStream();
                using var buffered = new MemoryStream();
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (buffered.Length + count > UserFileLimits.MaxUploadSizeBytes)
                    {
                        throw new InvalidOperationException("File exceeds the maximum upload size.");
                    }

                    await buffered.WriteAsync(buffer.AsMemory(0, count), ct);
                }

                buffered.Position = 0;
                using var reader = new StreamReader(buffered);
                var incoming = await reader.ReadToEndAsync(ct);
                var plaintext = protector.Unprotect(userScope.UserId, EncryptedFileContent.ProtectionPurpose, incoming) ?? incoming;
                return await SaveFileEditorTextAsync(userScope, new WriteFileTextRequest
                {
                    Path = normalized,
                    Content = plaintext,
                    Encrypt = true
                }, ct);
            }

            await using var stream = request.File.OpenReadStream();
            await userDirectoryService.UploadFileAsync(
                userScope.UserId,
                request.Path ?? string.Empty,
                request.File.FileName,
                stream);
            var relativePath = CombineRelativePath(request.Path, request.File.FileName);
            await HandleFileMutationAsync(userScope, relativePath);

            return NoContent();
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string path, CancellationToken ct, [FromQuery] FileRoot root = FileRoot.Home)
    {
        var userScope = await GetUserScopeAsync(ct);

        try
        {
            var stream = await userDirectoryService.DownloadFileAsync(userScope.UserId, path, root);
            return File(stream, "application/octet-stream", Path.GetFileName(path));
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    [HttpGet("text")]
    public async Task<IActionResult> ReadText([FromQuery] string path, CancellationToken ct, [FromQuery] FileRoot root = FileRoot.Home)
    {
        var userScope = await GetUserScopeAsync(ct);

        try
        {
            var content = await userDirectoryService.ReadFileTextAsync(userScope.UserId, path, root);
            var protection = await GetProtectionAsync(userScope.UserId, path, root, ct);
            var isEncrypted = EncryptedFileContent.TryDecrypt(
                protection.KeyOwner,
                path,
                content,
                protector,
                out var visibleContent,
                protection.Purpose);

            // Plaintext .enc files still open so a save encrypts them, but saving foreign ciphertext would wrap it twice.
            if (!isEncrypted && EncryptedFileContent.IsEncryptedPath(path) &&
                EncryptedFileContent.HasEnvelopeMarker(Encoding.UTF8.GetBytes(content)))
            {
                throw new InvalidOperationException("This encrypted file can't be opened. It was encrypted with a different key.");
            }

            return Ok(new FileTextDocument
            {
                Path = path,
                Content = visibleContent,
                IsEncrypted = isEncrypted
            });
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    [HttpPut("text")]
    public async Task<IActionResult> WriteText([FromBody] WriteFileTextRequest request, CancellationToken ct,
        [FromQuery] FileRoot root = FileRoot.Home)
    {
        var userScope = await GetUserScopeAsync(ct);
        if (System.Text.Encoding.UTF8.GetByteCount(request.Content) > UserFileLimits.MaxEditSizeBytes)
        {
            return BadRequest($"Content is too large to save (max {ByteSize.Format(UserFileLimits.MaxEditSizeBytes)}).");
        }

        if (root == FileRoot.Shared)
        {
            return await SaveSharedTextAsync(userScope.UserId, request, ct);
        }

        return await SaveFileEditorTextAsync(userScope, request, ct);
    }

    // Shared files are plain files with no personal-store dispatch, backups or Library reconciliation.
    private async Task<IActionResult> SaveSharedTextAsync(string userId, WriteFileTextRequest request, CancellationToken ct)
    {
        try
        {
            var content = request.Content;
            if (EncryptedFileContent.IsEncryptedPath(request.Path))
            {
                var protection = await GetProtectionAsync(userId, request.Path, FileRoot.Shared, ct);
                content = protector.Protect(protection.KeyOwner, protection.Purpose, request.Content);
            }

            await userDirectoryService.WriteFileTextAsync(userId, request.Path, content, FileRoot.Shared);
            return NoContent();
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    private Task<FileProtection> GetProtectionAsync(string userId, string path, FileRoot root, CancellationToken ct) =>
        vault.GetProtectionAsync(userId, Path.Combine(root == FileRoot.Shared
            ? userDirectoryService.GetSharedDirectoryPath()
            : userDirectoryService.GetUserDirectoryPath(userId), path), ct);

    [HttpGet("backups")]
    public async Task<IActionResult> ListBackups(
        [FromQuery] string path,
        CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);

        try
        {
            var backups = await userFileBackupService.ListBackupsAsync(userScope.UserId, path, ct);
            return Ok(backups.Select(ToSummary).ToList());
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    [HttpGet("backups/text")]
    public async Task<IActionResult> ReadBackupText(
        [FromQuery] string path,
        [FromQuery] string backupId,
        CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);

        try
        {
            var backup = await userFileBackupService.ReadBackupAsync(userScope.UserId, path, backupId, ct);

            // Decrypt only the preview so stored snapshots and restoration retain the original ciphertext.
            EncryptedFileContent.TryDecrypt(userScope.UserId, backup.Entry.RelativePath, backup.Content, protector, out var visibleContent);

            return Ok(new FileBackupTextDocument
            {
                Backup = ToSummary(backup.Entry),
                Content = visibleContent
            });
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    [HttpPost("backups/restore")]
    public async Task<IActionResult> RestoreBackup(
        [FromQuery] string path,
        [FromQuery] string backupId,
        CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);

        try
        {
            var restored = await userFileBackupService.RestoreBackupAsync(userScope.UserId, path, backupId, ct);
            await HandleFileMutationAsync(userScope, restored.RelativePath);
            return NoContent();
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }

    /// <summary>Returns safe provider and per-file state for the current user's Library index.</summary>
    [HttpGet("library-index")]
    public async Task<ActionResult<LibraryIndexView>> GetLibraryIndex(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var view = await userScope.LibraryIndexingService
            .GetSnapshotAsync(cancellationToken: ct);
        return Ok(view);
    }

    private async ValueTask HandleFileMutationAsync(
        UserServiceScope userScope,
        string userRelativePath,
        bool isDirectory = false)
    {
        InvalidatePromptCacheIfNeeded(userScope, userRelativePath);
        var normalized = CanonicalFilePath(userScope.UserId, userRelativePath);
        // Restores and deletion reconcile through the same index owner as concept edits.
        if (GetKnowledgeStore(userScope.UserId, normalized, isDirectory) is { } knowledge)
        {
            await knowledge.Store.RebuildIndexesAsync(knowledge.Document);
        }

        if (normalized.StartsWith(UserFileNames.AgentsDirectory + "/", StringComparison.OrdinalIgnoreCase))
        {
            await agentCatalog.ReloadUserAsync(userScope.UserId);
            await userScope.AppService.ReloadAgentDefinitionsAsync();
            userScope.AppService.InvalidateAllCachedAgents();
        }
        else if (normalized.StartsWith(UserFileNames.TasksDirectory + "/", StringComparison.OrdinalIgnoreCase))
        {
            await taskCatalog.ReloadUserAsync(userScope.UserId);
        }
        else if (normalized.StartsWith(UserFileNames.SettingsDirectory + "/", StringComparison.OrdinalIgnoreCase))
        {
            await userScope.AppService.ReloadUserSettingsAsync();
        }

        string? libraryRelativePath = GetLibraryRelativePath(userRelativePath);
        if (libraryRelativePath is null)
        {
            return;
        }

        // The file is already committed, so request cancellation must not suppress its indexing signal.
        try
        {
            await userScope.LibraryIndexingService.QueueReconciliationAsync(
                changedRelativePath: libraryRelativePath);
        }
        catch (Exception ex)
        {
            using var scope = BeginNotificationHandledScope(userScope.UserId);
            logger.LogWarning(
                ex,
                "Library reconciliation could not be queued after changing {RelativePath}.",
                userRelativePath);
        }
    }

    private IDisposable? BeginNotificationHandledScope(string userId) =>
        logger.BeginScope(new Dictionary<string, object?>
        {
            ["userId"] = userId,
            ["notificationHandled"] = true
        });

    private bool TryCreateProblem(Exception ex, out IActionResult result)
    {
        result = ex switch
        {
            FileNotFoundException => NotFound(ex.Message),
            DirectoryNotFoundException => NotFound(ex.Message),
            UnauthorizedAccessException => StatusCode(StatusCodes.Status403Forbidden, ex.Message),
            ArgumentException => BadRequest(ex.Message),
            InvalidOperationException => BadRequest(ex.Message),
            _ => Problem(ex.Message)
        };

        return true;
    }

    private static void InvalidatePromptCacheIfNeeded(UserServiceScope userScope, string relativePath)
    {
        if (IsRuntimePromptPath(relativePath))
        {
            userScope.AppService.InvalidateAllCachedAgents();
        }
    }

    private static bool IsRuntimePromptPath(string relativePath)
    {
        var normalizedPath = PathHelper.ToSlashSeparatedRelativePath(relativePath);
        return RuntimePromptPaths.Contains(normalizedPath);
    }

    private static string CombineRelativePath(string? parentPath, string fileName)
    {
        var relativePath = string.IsNullOrWhiteSpace(parentPath)
            ? fileName
            : Path.Combine(parentPath, fileName);
        return PathHelper.ToSlashSeparatedRelativePath(relativePath);
    }

    private static string? GetLibraryRelativePath(string userRelativePath)
    {
        var libraryRoot =
            $"{UserFileNames.WorkspaceDirectory}/{UserFileNames.LibraryWorkspaceDirectory}";
        return PathHelper.GetRelativePathUnderRoot(
            userRelativePath,
            libraryRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    private static FileBackupSummary ToSummary(UserFileBackupEntry entry) =>
        new()
        {
            Id = entry.Id,
            Path = entry.RelativePath,
            CreatedUtc = entry.CreatedUtc,
            SizeBytes = entry.SizeBytes
        };

    public sealed class FileUploadRequest
    {
        public string? Path { get; set; }
        public IFormFile? File { get; set; }
    }
    private string CanonicalFilePath(string userId, string relativePath)
    {
        var root = userDirectoryService.GetUserDirectoryPath(userId);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!PathHelper.IsPathWithinDirectory(fullPath, root) || PathHelper.HasSymlinkInPath(fullPath, root))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        return PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(root, fullPath));
    }
    /// <summary>Recognizes user memory, the backlog and private notes using their existing directory owners.</summary>
    private (EncryptedKnowledgeStore Store, string Document)? GetKnowledgeStore(string userId, string path, bool isDirectory = false)
    {
        if (!isDirectory && !path.EndsWith(".md.enc", StringComparison.OrdinalIgnoreCase)) { return null; }

        var parts = path.Split('/');
        // Native path equality keeps format validation on Windows casing aliases of the same file.
        string? root = PathHelper.ArePathsEqual(parts[0], UserFileNames.MemoryDirectory) ||
            PathHelper.ArePathsEqual(parts[0], UserFileNames.BacklogDirectory) ? parts[0] :
            parts.Length >= (isDirectory ? 4 : 5) && PathHelper.ArePathsEqual(parts[0], UserFileNames.AgentsDirectory) &&
            PathHelper.ArePathsEqual(parts[2], UserFileNames.AgentPrivateDirectory) && PathHelper.ArePathsEqual(parts[3], AgentNotesFile.NotesDirectory)
                ? string.Join('/', parts.Take(4)) : null;
        if (root is null || path.Length <= root.Length) { return null; }
        // Deleted directories rebuild surviving ancestors without recreating the removed directory.
        var document = path[(root.Length + 1)..];
        if (!isDirectory) { document = document[..^4]; }
        // Tool saves and Files edits share format validation, encryption, backups and derived indexes.
        return (new EncryptedKnowledgeStore(userId, userDirectoryService.GetUserDirectoryPath(userId), root,
            UserFileLimits.MaxEditSizeBytes, userFileBackupService, protector), document);
    }
    private async Task<IActionResult> SaveFileEditorTextAsync(UserServiceScope userScope, WriteFileTextRequest request, CancellationToken ct)
    {
        try
        {
            var normalized = CanonicalFilePath(userScope.UserId, request.Path);
            var segments = normalized.Split('/');
            if (segments.Length == 3 && string.Equals(segments[0], UserFileNames.AgentsDirectory, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(segments[2], UserFileNames.AgentFile, StringComparison.OrdinalIgnoreCase) || string.Equals(segments[2], UserFileNames.AgentToolPoliciesFile, StringComparison.OrdinalIgnoreCase)))
            {
                var edited = agentFiles.ParseFileText(userScope.UserId, normalized, request.Content);
                if (edited.IsMain)
                {
                    var previous = userScope.AppService.MainAgentDefinition;
                    userScope.AppService.MainAgentDefinition = edited;
                    try { await userScope.AppService.SaveMainAgentAsync(); }
                    catch
                    {
                        // Rejected file edits must not replace the active definition.
                        userScope.AppService.MainAgentDefinition = previous;
                        throw;
                    }
                }
                else if (!await userScope.AppService.SaveCustomAgentAsync(edited,
                    requireExisting: agentCatalog.TryGetAgentDirectory(edited.Id) is not null,
                    creationDirectoryName: agentCatalog.TryGetAgentDirectory(edited.Id) is null ? segments[1] : null))
                {
                    throw new InvalidOperationException("The selected agent is unavailable.");
                }

                await UserServices.RefreshAgentConsumersAsync(userScope.UserId, edited.Id);
            }
            else if (segments.Length == 3 && string.Equals(segments[0], UserFileNames.TasksDirectory, StringComparison.OrdinalIgnoreCase) && string.Equals(segments[2], UserFileNames.TaskFile, StringComparison.OrdinalIgnoreCase))
            {
                var entry = taskCatalog.GetTasksForUser(userScope.UserId).SingleOrDefault(candidate =>
                    string.Equals(Path.GetFileName(candidate.DirectoryPath), segments[1], OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                var existing = entry is null ? null : await userScope.AgentTaskRepository.GetTaskAsync(userScope.UserId, entry.TaskId, ct);
                var edited = userScope.AgentTaskRepository.ParseDefinition(userScope.UserId, request.Content);
                if (entry is not null && edited.Id != entry.TaskId) { throw new InvalidOperationException("The task ID cannot change in the file editor."); }

                if (entry is null && await userScope.AgentTaskRepository.GetTaskAsync(userScope.UserId, edited.Id, ct) is not null)
                {
                    throw new InvalidOperationException("A new definition requires a new task ID.");
                }

                var result = await userScope.TaskManagementService.SaveAsync(edited, existing, null, ct,
                    creationDirectoryName: entry is null ? segments[1] : null);
                if (result.Error is not null) { return BadRequest(result.Error); }
            }
            else if (segments.Length == 2 && string.Equals(segments[0], UserFileNames.SettingsDirectory, StringComparison.OrdinalIgnoreCase) &&
                segments[1].EndsWith(".yaml.enc", StringComparison.OrdinalIgnoreCase))
            {
                await settingsRepository.SaveSettingsAsync(userScope.UserId, segments[1][..^9], request.Content, ct);
            }
            else
            {
                if (IsRuntimePromptPath(normalized)) { PromptInstructionRecord.Parse(request.Content); }

                var knowledge = GetKnowledgeStore(userScope.UserId, normalized);
                if (knowledge is { } bundle)
                {
                    await bundle.Store.EditDocumentAsync(bundle.Document, request.Content, ct);
                    return NoContent();
                }

                // The path determines protection even when the editor opened a plaintext .enc file.
                var content = EncryptedFileContent.IsEncryptedPath(normalized)
                    ? protector.Protect(userScope.UserId, EncryptedFileContent.ProtectionPurpose, request.Content)
                    : request.Content;
                await userDirectoryService.WriteFileTextAsync(userScope.UserId, normalized, content);
            }

            await HandleFileMutationAsync(userScope, normalized);
            return NoContent();
        }
        catch (Exception ex) when (TryCreateProblem(ex, out var result))
        {
            return result;
        }
    }
}
