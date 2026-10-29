using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Users;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Memory;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Binds the shared knowledge store to the running agent's existing private notes directory.</summary>
public static class AgentNotesFile
{
    public const string NotesDirectory = "notes";
    /// <summary>Creates a notes store using trusted user and agent directory ownership.</summary>
    public static EncryptedKnowledgeStore CreateStore(string userId, string agentId, IUserDirectoryService directories,
        IContentProtector protector, IUserFileBackupService fileBackups)
    {
        var privatePath = directories.GetAgentPrivateDirectoryPath(agentId)
            ?? throw new UnauthorizedAccessException("The agent has no private directory.");
        var userRoot = directories.GetUserDirectoryPath(userId);
        return new EncryptedKnowledgeStore(userId, userRoot,
            Path.GetRelativePath(userRoot, Path.Combine(privatePath, NotesDirectory)),
            UserFileLimits.MaxEditSizeBytes, fileBackups, protector);
    }

    /// <summary>Reads the complete derived notes entry index.</summary>
    public static Task<string?> ReadAsync(string userId, string agentId, IUserDirectoryService directories,
        IContentProtector protector, IUserFileBackupService fileBackups, CancellationToken ct = default) =>
        CreateStore(userId, agentId, directories, protector, fileBackups).ReadAsync(ct: ct);
}
