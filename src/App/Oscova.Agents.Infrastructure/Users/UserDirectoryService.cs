using Oscova.Agents.Secrets;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Infrastructure.Tasks;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Users;

public class UserDirectoryService : IUserDirectoryService
{
    private const string AgentsRootDirectoryName = UserFileNames.AgentsDirectory;
    private const string ChatRootDirectoryName = UserFileNames.ChatDirectory;
    private const string LogsRootDirectoryName = UserFileNames.LogsDirectory;
    private const string MemoryRootDirectoryName = UserFileNames.MemoryDirectory;
    private const string PromptsRootDirectoryName = UserFileNames.PromptsDirectory;
    private const string SkillsRootDirectoryName = UserFileNames.SkillsDirectory;
    private const string TasksRootDirectoryName = UserFileNames.TasksDirectory;
    private const string WorkspaceRootDirectoryName = UserFileNames.WorkspaceDirectory;
    private const string FileHistoryRootDirectoryName = UserFileNames.FileHistoryDirectory;

    // Root directories that cannot be deleted (but contents within them can be managed)
    private static readonly HashSet<string> ProtectedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        AgentsRootDirectoryName,
        ChatRootDirectoryName,
        LogsRootDirectoryName,
        MemoryRootDirectoryName,
        PromptsRootDirectoryName,
        // Keep protected configuration inside its owning settings workflow.
        UserFileNames.SettingsDirectory,
        SkillsRootDirectoryName,
        TasksRootDirectoryName,
        WorkspaceRootDirectoryName,
        Path.Combine(WorkspaceRootDirectoryName, UserFileNames.LibraryWorkspaceDirectory),
        FileHistoryRootDirectoryName
    };

    private readonly IAgentCatalog _agentCatalog;
    private readonly IProfilePictureService _profilePictures;

    private readonly IContentProtector _protector;
    private readonly OperatorVaultAccess _vault;
    public UserDirectoryService(IAgentCatalog agentCatalog, IProfilePictureService profilePictures, IContentProtector protector,
        OperatorVaultAccess vault)
    {
        _agentCatalog = agentCatalog;
        _profilePictures = profilePictures;
        _protector = protector;
        _vault = vault;
    }

    public string GetUserDirectoryPath(string userId)
    {
        ValidateUserId(userId);
        return Path.Combine(PathUtil.UsersPath, userId);
    }

    public string GetSkillsDirectoryPath(string userId)
    {
        ValidateUserId(userId);
        return Path.Combine(PathUtil.UsersPath, userId, SkillsRootDirectoryName);
    }

    public string GetMemoryDirectoryPath(string userId)
    {
        ValidateUserId(userId);
        return Path.Combine(PathUtil.UsersPath, userId, MemoryRootDirectoryName);
    }

    public string GetPromptDirectoryPath(string userId)
    {
        ValidateUserId(userId);
        return Path.Combine(PathUtil.UsersPath, userId, PromptsRootDirectoryName);
    }

    public string GetLogsDirectoryPath(string userId)
    {
        ValidateUserId(userId);
        return Path.Combine(PathUtil.UsersPath, userId, LogsRootDirectoryName);
    }

    public string GetWorkspaceRootPath(string userId)
    {
        ValidateUserId(userId);
        return Path.Combine(PathUtil.UsersPath, userId, WorkspaceRootDirectoryName);
    }

    public string GetSystemSettingsDirectoryPath()
    {
        return PathUtil.SettingsPath;
    }

    public string GetSharedDirectoryPath()
    {
        Directory.CreateDirectory(PathUtil.SharedPath);
        return PathUtil.SharedPath;
    }

    public string GetWorkspaceDirectoryPath(string userId, string workspaceName)
    {
        ValidateUserId(userId);
        ValidateWorkspaceName(workspaceName);

        var workspaceRootPath = GetWorkspaceRootPath(userId);
        var workspacePath = Path.GetFullPath(Path.Combine(workspaceRootPath, workspaceName));
        if (!PathHelper.IsPathWithinDirectory(workspacePath, workspaceRootPath))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        // Match the broader file API path checks so named workspaces cannot escape through symlinked segments.
        if (PathHelper.HasSymlinkInPath(workspacePath, workspaceRootPath))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        return workspacePath;
    }

    public string? GetAgentPrivateDirectoryPath(string agentId)
    {
        var agentDirectory = _agentCatalog.TryGetAgentDirectory(agentId);
        return agentDirectory is null
            ? null
            : Path.Combine(agentDirectory, UserFileNames.AgentPrivateDirectory);
    }

    public IReadOnlyList<string> ListWorkspaces(string userId)
    {
        var workspaceRootPath = GetWorkspaceRootPath(userId);
        if (!Directory.Exists(workspaceRootPath))
        {
            return [];
        }

        return [.. Directory
            .EnumerateDirectories(workspaceRootPath)
            .Select(path => new DirectoryInfo(path).Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)!];
    }

    public async Task EnsureUserDirectoryExistsAsync(string userId)
    {
        ValidateUserId(userId);

        var userDir = Path.Combine(PathUtil.UsersPath, userId);
        Directory.CreateDirectory(userDir);
        Directory.CreateDirectory(Path.Combine(userDir, SkillsRootDirectoryName));
        Directory.CreateDirectory(Path.Combine(userDir, MemoryRootDirectoryName));
        Directory.CreateDirectory(Path.Combine(userDir, PromptsRootDirectoryName));
        Directory.CreateDirectory(Path.Combine(userDir, LogsRootDirectoryName));
        Directory.CreateDirectory(Path.Combine(userDir, WorkspaceRootDirectoryName, UserFileNames.DefaultWorkspaceDirectory));
        Directory.CreateDirectory(Path.Combine(userDir, WorkspaceRootDirectoryName, UserFileNames.LibraryWorkspaceDirectory));
        Directory.CreateDirectory(Path.Combine(userDir, WorkspaceRootDirectoryName, UserFileNames.ToolsWorkspaceDirectory));

        // User records retain customization, while current defaults stay with their providers.
        foreach (var promptPath in SeedContentProvider.GetPromptDefaults().Keys)
        {
            SeedFileIfMissing(userId, userDir, promptPath,
                PromptInstructionRecord.Format(InstructionSelection.Default, string.Empty));
        }

        _profilePictures.EnsureUserPicture(userId);
    }

    private void SeedFileIfMissing(string userId, string userDir, string relativePath, string defaultContent)
    {
        var filePath = Path.Combine(userDir, relativePath);
        if (!File.Exists(filePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            EncryptedFileContent.WriteAllText(userId, filePath, defaultContent, _protector);
        }
    }

    public Task<IReadOnlyList<FileSystemEntry>> ListDirectoryAsync(string userId, string relativePath, FileRoot root = FileRoot.Home)
    {
        var fullPath = ResolveAndValidatePath(userId, relativePath, root);
        var entries = new List<FileSystemEntry>();

        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult<IReadOnlyList<FileSystemEntry>>(entries);
        }

        // Times are UTC so the Files screen can show them in the user's chosen time zone, not the server's.
        var isRoot = PathHelper.ArePathsEqual(fullPath, GetRootPath(userId, root));
        foreach (var dir in Directory.EnumerateDirectories(fullPath))
        {
            var info = new DirectoryInfo(dir);
            if (isRoot && root == FileRoot.Home && IsProtectedRootDirectory(info.Name))
            {
                continue;
            }

            entries.Add(new FileSystemEntry(info.Name, true, 0, info.LastWriteTimeUtc));
        }

        foreach (var file in Directory.EnumerateFiles(fullPath))
        {
            var info = new FileInfo(file);
            entries.Add(new FileSystemEntry(info.Name, false, info.Length, info.LastWriteTimeUtc));
        }

        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return Task.FromResult<IReadOnlyList<FileSystemEntry>>(entries);
    }

    public async Task CreateFileAsync(string userId, string relativePath, string fileName, string? content = null,
        FileRoot root = FileRoot.Home)
    {
        ValidateFileName(fileName);
        var extension = Path.GetExtension(fileName);
        if (!UserEditableFileTypes.ContainsFileName(fileName))
        {
            throw new InvalidOperationException($"File type '{extension}' is not allowed. Allowed types: {UserEditableFileTypes.DisplayList}");
        }

        var rootPath = GetRootPath(userId, root);
        var dirPath = ResolveAndValidatePath(userId, relativePath, root);
        var filePath = Path.GetFullPath(Path.Combine(dirPath, fileName));

        if (!PathHelper.IsPathWithinDirectory(filePath, rootPath))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        ThrowIfProtectedRootPath(root, rootPath, filePath);

        // Files creation protects .enc content, including an initially empty document.
        if (EncryptedFileContent.IsEncryptedPath(filePath))
        {
            var protection = await _vault.GetProtectionAsync(userId, filePath).ConfigureAwait(false);
            content = _protector.Protect(protection.KeyOwner, protection.Purpose, content ?? string.Empty);
        }

        Directory.CreateDirectory(dirPath);

        // FileMode.CreateNew throws if the file already exists
        using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write);
        if (content is not null)
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            await writer.WriteAsync(content).ConfigureAwait(false);
        }
    }

    public Task CreateDirectoryAsync(string userId, string relativePath, string directoryName, FileRoot root = FileRoot.Home)
    {
        ValidateFileName(directoryName);

        var rootPath = GetRootPath(userId, root);
        var parentPath = ResolveAndValidatePath(userId, relativePath, root);
        var newDirPath = Path.GetFullPath(Path.Combine(parentPath, directoryName));

        if (!PathHelper.IsPathWithinDirectory(newDirPath, rootPath))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        ThrowIfProtectedRootPath(root, rootPath, newDirPath);

        Directory.CreateDirectory(newDirPath);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string userId, string relativePath, FileRoot root = FileRoot.Home)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Cannot delete the root directory.");
        }

        var fullPath = ResolveAndValidatePath(userId, relativePath, root);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("File not found.");
        }

        File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string userId, string relativePath, FileRoot root = FileRoot.Home)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Cannot delete the root directory.");
        }

        var fullPath = ResolveAndValidatePath(userId, relativePath, root);

        var userDir = GetRootPath(userId, root);

        // Protect against deleting the user root directory itself (e.g. relativePath = "skills/..")
        var normalizedUserDir = Path.GetFullPath(userDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFullPath = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedUserDir, normalizedFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot delete the root directory.");
        }

        // Protect reserved root directories, which exist only in a user's own folder
        var relFromUser = Path.GetRelativePath(userDir, fullPath);
        if (root == FileRoot.Home && ProtectedDirectories.Contains(relFromUser))
        {
            throw new InvalidOperationException($"The '{relFromUser}' directory is protected and cannot be deleted.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("Directory not found.");
        }

        Directory.Delete(fullPath, true);
        return Task.CompletedTask;
    }

    public Task<int> DeleteUserRootAsync(string userId, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        ct.ThrowIfCancellationRequested();

        var userDir = GetUserDirectoryPath(userId);
        if (!Directory.Exists(userDir))
        {
            return Task.FromResult(0);
        }

        // Reparse points are removed as links, but their targets must never be traversed while counting.
        var deletedFileCount = Directory.EnumerateFiles(
            userDir,
            "*",
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            }).Count();
        Directory.Delete(userDir, recursive: true);
        return Task.FromResult(deletedFileCount);
    }

    public async Task UploadFileAsync(string userId, string relativePath, string fileName, Stream content,
        FileRoot root = FileRoot.Home)
    {
        ValidateFileName(fileName);

        var rootPath = GetRootPath(userId, root);
        var dirPath = ResolveAndValidatePath(userId, relativePath, root);
        var filePath = Path.GetFullPath(Path.Combine(dirPath, fileName));

        if (!PathHelper.IsPathWithinDirectory(filePath, rootPath))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        ThrowIfProtectedRootPath(root, rootPath, filePath);

        // Every shared .enc file moves to the operator key, so other members can open an upload of the user's own file.
        var isShared = root == FileRoot.Shared;
        if (isShared
            ? EncryptedFileContent.IsEncryptedPath(filePath)
            : UserFileNames.IsEncryptedConfigurationPath(Path.GetRelativePath(rootPath, filePath)))
        {
            using var buffered = new MemoryStream();
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await content.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (buffered.Length + bytesRead > UserFileLimits.MaxUploadSizeBytes)
                {
                    throw new InvalidOperationException($"File exceeds the maximum upload size of {ByteSize.Format(UserFileLimits.MaxUploadSizeBytes)}.");
                }

                await buffered.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
            }

            buffered.Position = 0;
            using var reader = new StreamReader(buffered);
            var incoming = await reader.ReadToEndAsync().ConfigureAwait(false);
            var plaintext = _protector.Unprotect(userId, EncryptedFileContent.ProtectionPurpose, incoming);
            if (plaintext is null && isShared)
            {
                throw new InvalidOperationException("The encrypted file cannot be opened with your vault key, so it was not uploaded.");
            }

            var protection = await _vault.GetProtectionAsync(userId, filePath).ConfigureAwait(false);
            await FileUtil.WriteAllTextAtomicAsync(filePath,
                _protector.Protect(protection.KeyOwner, protection.Purpose, plaintext ?? incoming)).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(dirPath);

        // Stage to a temp file in the target directory so an over-limit or failed upload
        // never truncates an existing file or leaves a partial one. Promote only on success.
        var tempPath = FileUtil.CreateSiblingTempPath(filePath);
        try
        {
            var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (fileStream.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                var totalBytesRead = 0L;
                int bytesRead;
                while ((bytesRead = await content.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    totalBytesRead += bytesRead;
                    if (totalBytesRead > UserFileLimits.MaxUploadSizeBytes)
                    {
                        throw new InvalidOperationException($"File exceeds the maximum upload size of {ByteSize.Format(UserFileLimits.MaxUploadSizeBytes)}.");
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                }
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of the staging file; nothing actionable if it fails.
        }
    }

    public Task<Stream> DownloadFileAsync(string userId, string relativePath, FileRoot root = FileRoot.Home)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Cannot download a directory.");
        }

        var fullPath = ResolveAndValidatePath(userId, relativePath, root);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("File not found.");
        }

        return Task.FromResult<Stream>(new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public bool FileExists(string userId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var fullPath = ResolveAndValidatePath(userId, relativePath);
        return File.Exists(fullPath);
    }

    public async Task<string> ReadFileTextAsync(string userId, string relativePath, FileRoot root = FileRoot.Home)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("A file path is required.");
        }

        var fullPath = ResolveAndValidatePath(userId, relativePath, root);
        ValidateTextFileExtension(fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("File not found.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > GetStoredEditSizeLimit(fullPath))
        {
            throw new InvalidOperationException($"File is too large to edit (max {ByteSize.Format(UserFileLimits.MaxEditSizeBytes)}).");
        }

        return await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <summary>Reads a topic's custom body and applies its saved composition mode.</summary>
    public async Task<string?> ReadPromptInstructionAsync(string userId, string relativePath,
        string? defaultInstruction, CancellationToken ct = default)
    {
        if (!FileExists(userId, relativePath)) { return defaultInstruction; }

        var document = await ReadFileTextForRuntimeAsync(userId, relativePath, ct).ConfigureAwait(false);
        var (mode, content) = PromptInstructionRecord.Parse(document);
        return InstructionSelection.Select(mode, content, defaultInstruction);
    }

    public async Task<string> ReadFileTextForRuntimeAsync(
        string userId,
        string relativePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("A file path is required.");
        }

        var fullPath = ResolveAndValidatePath(userId, relativePath);
        ValidateTextFileExtension(fullPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("File not found.");
        }

        if (EncryptedFileContent.IsEncryptedPath(fullPath))
        {
            var text = await EncryptedFileContent.ReadAllTextAsync(userId, fullPath, _protector, ct).ConfigureAwait(false)
                ?? throw new FileNotFoundException("File not found.", fullPath);
            return text.Length > UserFileLimits.MaxEditSizeBytes ? text[..(int)UserFileLimits.MaxEditSizeBytes] : text;
        }

        using var reader = new StreamReader(fullPath);
        var buffer = new char[UserFileLimits.MaxEditSizeBytes];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await reader
                .ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return new string(buffer, 0, totalRead);
    }

    public async Task WriteFileTextAsync(string userId, string relativePath, string content, FileRoot root = FileRoot.Home)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("A file path is required.");
        }

        var fullPath = ResolveAndValidatePath(userId, relativePath, root);
        ValidateTextFileExtension(fullPath);

        if (System.Text.Encoding.UTF8.GetByteCount(content) > GetStoredEditSizeLimit(fullPath))
        {
            throw new InvalidOperationException($"Content is too large to save (max {ByteSize.Format(UserFileLimits.MaxEditSizeBytes)}).");
        }

        await FileUtil.WriteAllTextAtomicAsync(fullPath, content).ConfigureAwait(false);
    }

    public async Task AppendToFileAsync(string userId, string relativePath, string content)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("A file path is required.");
        }

        var fullPath = ResolveAndValidatePath(userId, relativePath);
        ValidateTextFileExtension(fullPath);

        var newContentBytes = System.Text.Encoding.UTF8.GetByteCount(content);
        if (newContentBytes > UserFileLimits.MaxEditSizeBytes)
        {
            throw new InvalidOperationException($"Content is too large to append (max {ByteSize.Format(UserFileLimits.MaxEditSizeBytes)}).");
        }

        long existingLength = 0;
        if (File.Exists(fullPath))
        {
            existingLength = new FileInfo(fullPath).Length;
        }

        if (existingLength + newContentBytes > UserFileLimits.MaxEditSizeBytes)
        {
            throw new InvalidOperationException($"Resulting file is too large to append (max {ByteSize.Format(UserFileLimits.MaxEditSizeBytes)}).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.AppendAllTextAsync(fullPath, content).ConfigureAwait(false);
    }

    // Resolve a relative path within the selected root and validate it stays contained
    private string ResolveAndValidatePath(string userId, string relativePath, FileRoot root = FileRoot.Home)
    {
        var userDir = GetRootPath(userId, root);
        var normalized = Path.GetFullPath(Path.Combine(userDir, relativePath ?? string.Empty));

        if (!PathHelper.IsPathWithinDirectory(normalized, userDir))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        if (PathHelper.HasSymlinkInPath(normalized, userDir))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        ThrowIfProtectedRootPath(root, userDir, normalized);

        return normalized;
    }

    private string GetRootPath(string userId, FileRoot root) =>
        root == FileRoot.Shared ? GetSharedDirectoryPath() : GetUserDirectoryPath(userId);

    // The protected names belong to personal file history, so Shared treats them as ordinary content.
    private static void ThrowIfProtectedRootPath(FileRoot root, string userDir, string fullPath)
    {
        if (root == FileRoot.Shared) { return; }

        var relativePath = PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(userDir, fullPath));
        var firstSegment = relativePath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstSegment is not null && IsProtectedRootDirectory(firstSegment))
        {
            throw new UnauthorizedAccessException("Access denied.");
        }
    }

    // File history and the bulk-delete knowledge snapshots (memory.bak, backlog.bak) are support data, never Files content.
    private static bool IsProtectedRootDirectory(string name) =>
        string.Equals(name, FileHistoryRootDirectoryName, StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);

    // Validate userId is a valid GUID to prevent path traversal
    private static void ValidateUserId(string userId)
    {
        if (!Guid.TryParse(userId, out _))
        {
            throw new ArgumentException("Invalid user ID format.", nameof(userId));
        }
    }

    private static void ValidateFileName(string name) => FileNameValidator.ThrowIfInvalid(name);

    private static void ValidateWorkspaceName(string workspaceName)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
        {
            throw new ArgumentException("Workspace name is required.", nameof(workspaceName));
        }

        if (string.Equals(workspaceName, UserFileNames.AgentPrivateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("'private' is a reserved name.", nameof(workspaceName));
        }

        FileNameValidator.ThrowIfInvalid(workspaceName);
    }

    private static void ValidateTextFileExtension(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        if (!UserEditableFileTypes.ContainsFileName(fullPath))
        {
            throw new InvalidOperationException($"File type '{extension}' cannot be edited.");
        }
    }

    // Encrypted files are stored as a Base64 envelope, so their stored limit covers a full-size plaintext.
    private static long GetStoredEditSizeLimit(string path) =>
        EncryptedFileContent.IsEncryptedPath(path)
            ? UserFileLimits.MaxEncryptedEditSizeBytes
            : UserFileLimits.MaxEditSizeBytes;
}
