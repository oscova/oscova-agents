using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Reads, writes, edits, and patches files inside host-approved workspaces.
/// </summary>
/// <remarks>
/// Mutating operations declare approval requirements and all path parameters are still validated by workspace path middleware before execution.
/// </remarks>
public sealed class CodingToolset : TextFileToolsetBase, ICrewToolset
{
    private readonly Func<IReadOnlyList<AllowedDirectory>> _workspaceResolver;

    /// <summary>Creates file tools that resolve agent-facing paths against the active run's allowed directories.</summary>
    public CodingToolset(Func<IReadOnlyList<AllowedDirectory>>? workspaceResolver = null)
    {
        _workspaceResolver = workspaceResolver ?? (() => []);
    }

    /// <summary>Reads a page of text from a workspace-approved file path using one-based line numbers.</summary>
    [DisplayName("read_file")]
    [Description("Reads a file with 1-indexed line numbers and pagination.")]
    public string ReadFile(
        [Description("File path: relative to your working directory, or an absolute path listed under Workspace Access")]
        [WorkspacePath(Access = WorkspacePathAccess.Read)] string file_path,
        [Description("1-indexed line number to start reading from")] int offset = 1,
        [Description("Maximum number of lines to return")] int limit = 2000)
        => Execute(() => ReadFileCore(file_path, offset, limit), "The file could not be read.");

    /// <summary>Creates, overwrites, or appends text to a workspace-approved file path.</summary>
    [DisplayName("write_file")]
    [Description("Creates or overwrites a file, optionally appending content.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    public string WriteFile(
        [Description("File path: relative to your working directory, or an absolute path listed under Workspace Access")]
        [WorkspacePath(Access = WorkspacePathAccess.Write)] string file_path,
        [Description("Text content to write")] string content,
        [Description("Append instead of overwrite")] bool append = false)
        => Execute(() => WriteFileCore(file_path, content, append), "The file could not be written.");

    /// <summary>Replaces text in a file, requiring a unique match unless all matches are requested.</summary>
    [DisplayName("edit_file")]
    [Description("Replaces text in a file, requiring a unique match unless replace_all is true.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    public string EditFile(
        [Description("File path: relative to your working directory, or an absolute path listed under Workspace Access")]
        [WorkspacePath(Access = WorkspacePathAccess.Write)] string file_path,
        [Description("The text to find")] string old_string,
        [Description("The replacement text")] string new_string,
        [Description("Replace all occurrences")] bool replace_all = false)
        => Execute(() => EditFileCore(file_path, old_string, new_string, replace_all), "The file could not be edited.");

    /// <summary>Applies a structured multi-file patch relative to an approved workspace base directory.</summary>
    [DisplayName("apply_patch")]
    [Description("Applies a structured multi-file patch relative to a workspace base directory.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    public string ApplyPatch(
        [Description("Patch content in the supported apply_patch format")] string patch,
        [Description("Directory path: relative to your working directory, or an absolute path listed under Workspace Access")]
        [WorkspacePath(Access = WorkspacePathAccess.Write)] string base_directory)
        => Execute(() => ApplyPatchCore(patch, base_directory), "The patch could not be fully applied.");

    /// <inheritdoc />
    protected override TextReader OpenText(string path, out long sizeBytes)
    {
        var hostPath = ResolveHostPath(path);
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException("File not found.", path);
        }

        sizeBytes = new FileInfo(hostPath).Length;
        return new StreamReader(hostPath);
    }

    /// <inheritdoc />
    protected override void WriteText(string path, string content, bool append)
    {
        var hostPath = ResolveHostPath(path);
        var directory = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (append)
        {
            File.AppendAllText(hostPath, content);
        }
        else
        {
            FileUtil.WriteAllTextAtomic(hostPath, content);
        }
    }

    /// <inheritdoc />
    protected override void DeleteText(string path)
    {
        var hostPath = ResolveHostPath(path);
        if (File.Exists(hostPath))
        {
            File.Delete(hostPath);
        }
    }

    /// <inheritdoc />
    protected override void MoveText(string sourcePath, string targetPath, string content, bool contentChanged)
    {
        var sourceHostPath = ResolveHostPath(sourcePath);
        var targetHostPath = ResolveHostPath(targetPath);
        var targetDirectory = Path.GetDirectoryName(targetHostPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        FileUtil.WriteAllTextAtomic(targetHostPath, content);
        if (!string.Equals(sourceHostPath, targetHostPath, StringComparison.Ordinal))
        {
            File.Delete(sourceHostPath);
        }
    }

    /// <inheritdoc />
    protected override string ResolvePatchPath(string path, string baseDirectory) =>
        ResolveWorkspacePatchPath(path, baseDirectory, _workspaceResolver);

    private string ResolveHostPath(string path) => PathHelper.ResolveToHostPath(path, _workspaceResolver);

    private static string Execute(Func<string> operation, string failureReason)
    {
        try
        {
            return operation();
        }
        catch (FileNotFoundException)
        {
            throw new ToolFailureException("The requested file was not found.", "Check the path and try again.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ToolFailureException(failureReason, "Check the input and workspace access, then try again.");
        }
    }
}
