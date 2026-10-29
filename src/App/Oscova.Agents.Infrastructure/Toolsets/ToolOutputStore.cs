using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Secrets;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Saves oversized tool output in the current run's read-only folder, encrypted for every persisted run.</summary>
public sealed class ToolOutputStore(
    RunContextAccessor runContextAccessor,
    IContentProtector contentProtector,
    ILogger<ToolOutputStore> logger) : IToolOutputStore
{
    /// <summary>The mount label that identifies the run's output folder.</summary>
    public const string DirectoryLabel = "tool output for this run";

    /// <summary>The folder name used under thread and task storage.</summary>
    public const string FolderName = "tool-output";

    /// <summary>Creates the folder and returns it as a read-only run directory.</summary>
    public static AllowedDirectory CreateRunDirectory(string hostPath)
    {
        Directory.CreateDirectory(hostPath);
        return new AllowedDirectory(hostPath, DirectoryLabel, DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly);
    }

    /// <inheritdoc />
    public string? Save(string label, string content)
    {
        var run = runContextAccessor.Current;
        var folder = run?.StandardRunDirectories.FirstOrDefault(directory => directory.Label == DirectoryLabel);
        if (folder is null)
        {
            return null;
        }

        string fileName = $"{label}-{Guid.NewGuid().ToString("N")[..8]}.txt";
        fileName += EncryptedFileContent.FileExtension;
        content = contentProtector.Protect(run!.UserId, EncryptedFileContent.ProtectionPurpose, content);

        string hostPath = Path.Combine(folder.Value, fileName);
        try
        {
            FileUtil.WriteAllTextAtomic(hostPath, content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to save {Label} tool output in {Folder}.", label, folder.Value);
            return null;
        }

        return PathHelper.ResolveToAgentPath(hostPath, run.AllowedDirectories);
    }
}
