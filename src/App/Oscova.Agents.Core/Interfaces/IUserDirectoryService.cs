namespace Oscova.Agents.Core.Interfaces;

public interface IUserDirectoryService
{
    string GetUserDirectoryPath(string userId);
    string GetSkillsDirectoryPath(string userId);
    string GetMemoryDirectoryPath(string userId);
    string GetPromptDirectoryPath(string userId);
    string GetLogsDirectoryPath(string userId);
    string GetWorkspaceRootPath(string userId);
    string GetWorkspaceDirectoryPath(string userId, string workspaceName);
    string? GetAgentPrivateDirectoryPath(string agentId);
    string GetSystemSettingsDirectoryPath();
    /// <summary>Returns the host-wide shared folder, creating it on first use.</summary>
    string GetSharedDirectoryPath();
    IReadOnlyList<string> ListWorkspaces(string userId);
    Task EnsureUserDirectoryExistsAsync(string userId);

    // File browser operations
    Task<IReadOnlyList<FileSystemEntry>> ListDirectoryAsync(string userId, string relativePath, FileRoot root = FileRoot.Home);
    Task CreateFileAsync(string userId, string relativePath, string fileName, string? content = null, FileRoot root = FileRoot.Home);
    Task CreateDirectoryAsync(string userId, string relativePath, string directoryName, FileRoot root = FileRoot.Home);
    Task DeleteFileAsync(string userId, string relativePath, FileRoot root = FileRoot.Home);
    Task DeleteDirectoryAsync(string userId, string relativePath, FileRoot root = FileRoot.Home);
    Task<int> DeleteUserRootAsync(string userId, CancellationToken ct = default);
    Task UploadFileAsync(string userId, string relativePath, string fileName, Stream content, FileRoot root = FileRoot.Home);
    Task<Stream> DownloadFileAsync(string userId, string relativePath, FileRoot root = FileRoot.Home);
    bool FileExists(string userId, string relativePath);
    Task<string> ReadFileTextAsync(string userId, string relativePath, FileRoot root = FileRoot.Home);
    /// <summary>Reads a bounded text prefix for runtime context without applying the editor size rejection.</summary>
    Task<string> ReadFileTextForRuntimeAsync(string userId, string relativePath, CancellationToken ct = default)
        => ReadFileTextAsync(userId, relativePath);
    /// <summary>Resolves a topic record before its consumer renders instruction variables.</summary>
    Task<string?> ReadPromptInstructionAsync(string userId, string relativePath, string? defaultInstruction, CancellationToken ct = default);
    Task WriteFileTextAsync(string userId, string relativePath, string content, FileRoot root = FileRoot.Home);

    Task AppendToFileAsync(string userId, string relativePath, string content);
}
