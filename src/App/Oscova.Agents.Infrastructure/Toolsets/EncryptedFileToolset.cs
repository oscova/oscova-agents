using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Policies;
using Oscova.Agents.Secrets;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Reads, mutates and searches encrypted text files in the active agent workspace.</summary>
public sealed class EncryptedFileToolset(
    string userId,
    string operatorId,
    IContentProtector protector,
    Func<IReadOnlyList<AllowedDirectory>> workspaceResolver,
    IWorkspaceFileAccess? fileAccess = null,
    IToolOutputStore? outputStore = null) : TextFileToolsetBase(workspaceResolver, fileAccess, outputStore), IAgentToolset
{
    private const string EncryptedPathDescription =
        "Full file path ending with .enc; keep the original extension before it, for example notes.md.enc";

    /// <summary>Searches decrypted encrypted-file contents for a regular expression.</summary>
    [DisplayName("grep")]
    [AgentTool]
    [Description("Searches decrypted .enc file contents with a regex pattern. Only .enc files are searched.")]
    public string Grep(
        [Description("Regex pattern to search for")] string pattern,
        [Description("Directory or file to search in. Defaults to your working directory.")]
        [WorkspacePath(Access = WorkspacePathAccess.Read)] string? path = null,
        [Description("Optional glob filter matching the full file name, such as *.md.enc")] string? include = null,
        [Description("files_with_matches, content, or count")] string output_mode = "files_with_matches",
        [Description("Context lines before and after matches when output_mode is content")] int context_lines = 0,
        [Description("Maximum number of results to return")] int limit = 100) =>
        Execute(() => GrepCore(pattern, path, include, output_mode, context_lines, limit));

    /// <summary>Finds encrypted files by glob pattern and returns recent matches first.</summary>
    [DisplayName("glob")]
    [AgentTool]
    [Description("Finds .enc files by glob pattern, such as **/*.enc, sorted by last modified time. Other files are not returned.")]
    public string Glob(
        [Description("Glob pattern such as **/*.enc")] string pattern,
        [Description("Directory to search in. Defaults to your working directory.")]
        [WorkspacePath(Access = WorkspacePathAccess.Read)] string? path = null) =>
        Execute(() => GlobCore(pattern, path));

    /// <inheritdoc />
    protected override bool IsSearchable(string hostPath) => EncryptedFileContent.IsEncryptedPath(hostPath);

    /// <inheritdoc />
    protected override TextReader? OpenSearchText(string hostPath) => OpenText(hostPath, out _);

    /// <summary>Reads a page from an encrypted text file using one-based line numbers.</summary>
    [DisplayName("read")]
    [AgentTool]
    [Description("Reads an encrypted .enc file with 1-indexed line numbers and pagination.")]
    public string ReadFile(
        [Description(EncryptedPathDescription)]
        [WorkspacePath(Access = WorkspacePathAccess.Read)] string file_path,
        [Description("1-indexed line number to start reading from")] int offset = 1,
        [Description("Maximum number of lines to return")] int limit = 2000)
        => Execute(() => ReadFileCore(RequireEncryptedPath(file_path), offset, limit));

    /// <summary>Creates, overwrites, or appends to an encrypted text file.</summary>
    [DisplayName("write")]
    [AgentTool]
    [Description("Creates or overwrites an encrypted .enc file, optionally appending content.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.AutoApprove)]
    public string WriteFile(
        [Description(EncryptedPathDescription)]
        [WorkspacePath(Access = WorkspacePathAccess.Write)] string file_path,
        [Description("Text content to encrypt and write")] string content,
        [Description("Append instead of overwrite")] bool append = false)
        => Execute(() => WriteFileCore(RequireEncryptedPath(file_path), content, append), artifactPath: file_path);

    /// <summary>Replaces exact text in an encrypted text file.</summary>
    [DisplayName("edit")]
    [AgentTool]
    [Description("Replaces text in an encrypted .enc file, requiring a unique match unless replace_all is true.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.AutoApprove)]
    public string EditFile(
        [Description(EncryptedPathDescription)]
        [WorkspacePath(Access = WorkspacePathAccess.Write)] string file_path,
        [Description("The text to find")] string old_string,
        [Description("The replacement text")] string new_string,
        [Description("Replace all occurrences")] bool replace_all = false)
        => Execute(() => EditFileCore(RequireEncryptedPath(file_path), old_string, new_string, replace_all), artifactPath: file_path);

    /// <summary>Applies a structured multi-file patch to encrypted text files.</summary>
    [DisplayName("apply_patch")]
    [AgentTool]
    [Description("Applies a structured multi-file patch to encrypted .enc files, for example notes.md.enc.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    public string ApplyPatch(
        [Description("Patch content in the supported apply_patch format; every file path must end with .enc")] string patch,
        [Description("Directory path: relative to your working directory, or an absolute path within a listed granted folder")]
        [WorkspacePath(Access = WorkspacePathAccess.Write)] string base_directory)
        => Execute(() => ApplyPatchCore(patch, base_directory), isPatch: true, artifactPath: base_directory);

    // Tools receive agent paths such as /shared/notes.md.enc, and the key follows the granted host path they resolve to.
    private FileProtection GetProtection(string path)
    {
        var directories = WorkspaceResolver();
        return PathHelper.TryResolveToHostPath(path, directories, PathHelper.ResolveWorkingDirectoryHostPath(directories), out var hostPath)
            ? OperatorVaultAccess.GetProtection(userId, operatorId, hostPath)
            : new(userId, EncryptedFileContent.ProtectionPurpose);
    }

    /// <inheritdoc />
    protected override TextReader OpenText(string path, out long sizeBytes)
    {
        using Stream stream = Files.OpenRead(path);
        using var reader = new StreamReader(stream);
        var protection = GetProtection(path);
        var plaintext = protector.Unprotect(protection.KeyOwner, protection.Purpose, reader.ReadToEnd())
            ?? throw new InvalidDataException("Encrypted file data could not be opened.");
        sizeBytes = Encoding.UTF8.GetByteCount(plaintext);
        return new StringReader(plaintext);
    }

    /// <inheritdoc />
    protected override void WriteText(string path, string content, bool append)
    {
        if (append && Files.GetEntry(path) is not null)
        {
            using var reader = OpenText(path, out _);
            content = reader.ReadToEnd() + content;
        }

        var protection = GetProtection(path);
        var ciphertext = protector.Protect(protection.KeyOwner, protection.Purpose, content);
        Files.Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(ciphertext);
        });
    }

    /// <inheritdoc />
    protected override void DeleteText(string path)
    {
        Files.Delete(path);
    }

    /// <inheritdoc />
    protected override void MoveText(string sourcePath, string targetPath, string content, bool contentChanged)
    {
        WriteText(targetPath, content, append: false);
        if (!PathHelper.ArePathsEqual(sourcePath, targetPath))
        {
            Files.Delete(sourcePath);
        }
    }

    /// <inheritdoc />
    protected override string ResolvePatchPath(string path, string baseDirectory) =>
        ResolveWorkspacePatchPath(RequireEncryptedPath(path), baseDirectory, WorkspaceResolver);

    private static string RequireEncryptedPath(string path)
    {
        if (!EncryptedFileContent.IsEncryptedPath(path))
        {
            throw new ToolFailureException(
                "Encrypted file paths must end with '.enc'.",
                "Use a path such as 'notes.md.enc', or use the regular file tools for other files.");
        }

        return path;
    }

    private void EnsureUnlocked()
    {
        if (!protector.IsAvailable(userId))
        {
            throw new ConfidentialDataLockedException(
                "Unlock your Vault Password before using encrypted files.");
        }
    }

    private string Execute(Func<string> operation, bool isPatch = false, string? artifactPath = null,
        [CallerMemberName] string methodName = "")
    {
        try
        {
            EnsureUnlocked();
            var result = operation();
            if (artifactPath is not null && FunctionInvokingChatClient.CurrentContext?.CallContent is { } call &&
                call.Name == ToolNaming.BuildToolsetQualifiedName<EncryptedFileToolset>(methodName))
            {
                // Encrypted files stay on the server and may use a project granted on that same server.
                var directories = WorkspaceResolver();
                call.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                call.AdditionalProperties[ProjectToolRouter.FileEnvironmentMetadataKey] = "workspace";
                if (PathHelper.TryResolveToHostPath(artifactPath, directories,
                    PathHelper.ResolveWorkingDirectoryHostPath(directories), out var hostPath))
                {
                    var canonicalPath = PathHelper.ResolveToAgentPath(hostPath, directories);
                    call.AdditionalProperties[ProjectToolRouter.FilePathMetadataKey] = canonicalPath;
                    call.AdditionalProperties[ProjectToolRouter.FileEnvironmentMetadataKey] =
                        canonicalPath == "/project" || canonicalPath.StartsWith("/project/", StringComparison.Ordinal)
                            ? "project" : "workspace";
                }
            }

            return result;
        }
        catch (ConfidentialDataLockedException)
        {
            throw new ToolFailureException(
                "Encrypted files are locked.",
                "Unlock your Vault Password and try again.");
        }
        catch (FileNotFoundException)
        {
            throw new ToolFailureException("The encrypted file was not found.", "Check the path and try again.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new ToolFailureException(isPatch
                ? "The encrypted file patch could not be applied."
                : "The encrypted file path or input is invalid.");
        }
        catch (InvalidOperationException) when (isPatch)
        {
            throw new ToolFailureException("The encrypted file patch could not be applied.");
        }
        catch (InvalidDataException)
        {
            throw new ToolFailureException("The encrypted file data is corrupted or unavailable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ToolFailureException(
                "Encrypted file storage is unavailable.",
                "Check workspace access and try again.");
        }
    }
}
