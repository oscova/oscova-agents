using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Infrastructure.Projects;

/// <summary>Reads bounded repository comparisons without changing the index or executing configured filters.</summary>
public sealed class ProjectGitService(ILogger<ProjectGitService> logger)
{
    private const int MaxStatusBytes = 2 * 1024 * 1024;
    private const int MaxStatusEntries = 2000;

    /// <summary>Reads staged, unstaged, and untracked entries from the selected repository root.</summary>
    public async Task<ProjectChanges> GetChangesAsync(ProjectDefinition project, CancellationToken ct = default)
    {
        try
        {
            var disabledFilters = new List<string>();
            var state = await ValidateAsync(project.RootPath, disabledFilters, ct).ConfigureAwait(false);
            if (state != ProjectReadState.Ready) { return new(state, []); }

            var result = await RunAsync(project.RootPath, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all"], MaxStatusBytes, ct, disabledFilters).ConfigureAwait(false);
            if (result.ExitCode != 0) { return new(ProjectReadState.Unavailable, []); }

            var fields = result.Text.Split('\0');
            var entries = new List<ProjectChange>();
            for (var i = 0; i < fields.Length - 1; i++)
            {
                var field = fields[i];
                if (field.Length < 4 || field[2] != ' ') { return new(ProjectReadState.Unavailable, []); }

                var status = field[..2];
                var path = field[3..];
                string? oldPath = null;
                if (status.Contains('R') || status.Contains('C'))
                {
                    if (++i >= fields.Length - 1) { return new(ProjectReadState.Unavailable, []); }

                    oldPath = fields[i];
                }

                if (status == "??") { entries.Add(new(path, null, status, "untracked")); }
                else
                {
                    if (status[0] != ' ') { entries.Add(new(path, oldPath, status, "staged")); }

                    if (status[1] != ' ') { entries.Add(new(path, oldPath, status, "unstaged")); }
                }

                if (entries.Count >= MaxStatusEntries)
                {
                    return new(ProjectReadState.Ready, entries.Take(MaxStatusEntries).ToArray(), i < fields.Length - 2 || entries.Count > MaxStatusEntries);
                }
            }

            return new(ProjectReadState.Ready, entries);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            return new(ProjectReadState.GitMissing, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Cannot read Git status for project {ProjectId}.", project.Id);
            return new(ex is InvalidDataException ? ProjectReadState.TooLarge : ProjectReadState.Unavailable, []);
        }
    }

    /// <summary>Rechecks the selected entry before comparing its exact local object and working-file versions.</summary>
    public async Task<ProjectChangePreview> GetChangeAsync(ProjectDefinition project, ProjectChange selection,
        IReadOnlyList<AllowedDirectory> directories, CancellationToken ct = default)
    {
        var status = await GetChangesAsync(project, ct).ConfigureAwait(false);
        if (status.State != ProjectReadState.Ready) { return new(status.State); }

        var entry = status.Entries.FirstOrDefault(candidate => candidate == selection);
        if (entry is null) { return new(ProjectReadState.RefreshRequired); }

        if (entry.Status.Contains('U') || entry.Status is "AA" or "DD") { return new(ProjectReadState.Unsupported); }

        try
        {
            var root = new AllowedDirectory(project.RootPath, project.Id, DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly);
            var path = ProjectAccessService.ResolveRelativePath(root, entry.Path);
            var files = new WorkspaceFileAccess(() => directories);
            var metadata = files.GetEntry(path);
            if (metadata is not null && (metadata.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                return new(ProjectReadState.Unsupported);
            }

            ProjectFilePreview before;
            ProjectFilePreview after;
            if (entry.Group == "staged")
            {
                before = await ReadBlobAsync(project.RootPath, entry.OldPath ?? entry.Path, fromIndex: false, ct).ConfigureAwait(false);
                after = await ReadBlobAsync(project.RootPath, entry.Path, fromIndex: true, ct).ConfigureAwait(false);
            }
            else
            {
                before = entry.Group == "untracked"
                    ? new(ProjectReadState.Ready)
                    : await ReadBlobAsync(project.RootPath, entry.Status[1] is 'R' or 'C' ? entry.OldPath ?? entry.Path : entry.Path, fromIndex: true, ct).ConfigureAwait(false);
                if (metadata is null) { after = new(ProjectReadState.Ready); }
                else
                {
                    using var stream = files.OpenRead(path);
                    after = await ProjectAccessService.ReadTextAsync(stream, ct).ConfigureAwait(false);
                }
            }

            if (before.State != ProjectReadState.Ready) { return new(before.State); }

            if (after.State != ProjectReadState.Ready) { return new(after.State); }

            var refreshed = await GetChangesAsync(project, ct).ConfigureAwait(false);
            if (refreshed.State != ProjectReadState.Ready || !refreshed.Entries.Contains(entry))
            {
                return new(ProjectReadState.RefreshRequired);
            }

            return new(ProjectReadState.Ready, before.Text, after.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "Cannot read Git comparison for project {ProjectId}.", project.Id);
            return new(ex is InvalidDataException ? ProjectReadState.TooLarge : ProjectReadState.Unavailable);
        }
    }

    private async Task<ProjectReadState> ValidateAsync(string root, List<string> disabledFilters, CancellationToken ct)
    {
        var git = Path.Combine(root, ".git");
        if (!Directory.Exists(git))
        {
            for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            {
                var metadata = Path.Combine(ancestor.FullName, ".git");
                if (File.Exists(metadata) || Directory.Exists(metadata)) { return ProjectReadState.Unsupported; }
            }

            return ProjectReadState.NotRepository;
        }

        if (PathHelper.HasSymlinkInPath(git, Path.GetPathRoot(git)!)) { return ProjectReadState.Unsupported; }

        foreach (var name in new[] { "config", "index", "HEAD", "packed-refs", "shallow", "objects", "refs", "info" })
        {
            if (PathHelper.HasSymlinkInPath(Path.Combine(git, name), git)) { return ProjectReadState.Unsupported; }
        }
        // Git may read loose objects, packs, and symbolic refs during status as well as blob reads.
        var pending = new Stack<string>(new[] { Path.Combine(git, "objects"), Path.Combine(git, "refs"), Path.Combine(git, "info") });
        while (pending.TryPop(out var directory))
        {
            if (!Directory.Exists(directory)) { continue; }

            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { return ProjectReadState.Unsupported; }

                if ((entry.Attributes & FileAttributes.Directory) != 0) { pending.Push(entry.FullName); }
            }
        }

        if (File.Exists(Path.Combine(git, "commondir")) || File.Exists(Path.Combine(git, "config.worktree")) ||
            File.Exists(Path.Combine(git, "objects", "info", "alternates")) ||
            File.Exists(Path.Combine(git, "objects", "info", "http-alternates")))
        {
            return ProjectReadState.Unsupported;
        }

        var config = await RunAsync(root, ["config", "--no-includes", "--null", "--list"], MaxStatusBytes, ct).ConfigureAwait(false);
        if (config.ExitCode != 0) { return ProjectReadState.Unavailable; }

        foreach (var item in config.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = item.Split('\n', 2)[0];
            if (key.StartsWith("include", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("extensions.worktreeconfig", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("core.worktree", StringComparison.OrdinalIgnoreCase))
            {
                return ProjectReadState.Unsupported;
            }

            if (key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) &&
                (key.EndsWith(".clean", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".process", StringComparison.OrdinalIgnoreCase)))
            {
                // An attribute change between validation and status must not execute a registered command.
                disabledFilters.Add(key + "=");
            }
        }

        var top = await RunAsync(root, ["rev-parse", "--show-toplevel"], 8192, ct).ConfigureAwait(false);
        if (top.ExitCode != 0 || !PathHelper.ArePathsEqual(top.Text.TrimEnd('\r', '\n'), root)) { return ProjectReadState.Unsupported; }

        var paths = await RunAsync(root, ["ls-files", "--cached", "--others", "--exclude-standard", "--deduplicate", "-z"], MaxStatusBytes, ct).ConfigureAwait(false);
        if (paths.ExitCode != 0) { return ProjectReadState.Unavailable; }

        var attributes = await RunAsync(root, ["check-attr", "--stdin", "-z", "filter"], MaxStatusBytes, ct, input: paths.Bytes).ConfigureAwait(false);
        if (attributes.ExitCode != 0) { return ProjectReadState.Unavailable; }

        var fields = attributes.Text.Split('\0');
        if ((fields.Length - 1) % 3 != 0) { return ProjectReadState.Unavailable; }

        for (var i = 0; i < fields.Length - 1; i += 3)
        {
            if (fields[i + 1] != "filter") { return ProjectReadState.Unavailable; }

            if (fields[i + 2] is not ("unspecified" or "unset" or "set")) { return ProjectReadState.Unsupported; }
        }

        return ProjectReadState.Ready;
    }

    private async Task<ProjectFilePreview> ReadBlobAsync(string root, string path, bool fromIndex, CancellationToken ct)
    {
        var state = await ValidateAsync(root, [], ct).ConfigureAwait(false);
        if (state != ProjectReadState.Ready) { return new(state); }

        if (!fromIndex)
        {
            var head = await RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], 8192, ct).ConfigureAwait(false);
            if (head.ExitCode == 1) { return new(ProjectReadState.Ready); }

            if (head.ExitCode != 0) { return new(ProjectReadState.Unavailable); }
        }

        var metadata = await RunAsync(root, fromIndex
            ? ["ls-files", "--stage", "-z", "--", path]
            : ["ls-tree", "-z", "HEAD", "--", path], MaxStatusBytes, ct).ConfigureAwait(false);
        if (metadata.ExitCode != 0) { return new(ProjectReadState.Unavailable); }

        var records = metadata.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length == 0) { return new(ProjectReadState.Ready); }

        if (records.Length != 1) { return new(ProjectReadState.Unsupported); }

        var tab = records[0].IndexOf('\t');
        if (tab < 0 || records[0][(tab + 1)..] != path) { return new(ProjectReadState.Unavailable); }

        var parts = records[0][..tab].Split(' ');
        if (parts.Length != 3 || parts[0] is not ("100644" or "100755")) { return new(ProjectReadState.Unsupported); }

        var oid = parts[fromIndex ? 1 : 2];
        if (oid.Length is not (40 or 64) || !oid.All(Uri.IsHexDigit)) { return new(ProjectReadState.Unavailable); }

        var size = await RunAsync(root, ["cat-file", "-s", oid], 128, ct).ConfigureAwait(false);
        if (size.ExitCode != 0 || !long.TryParse(size.Text.Trim(), out var bytes)) { return new(ProjectReadState.Unavailable); }

        if (bytes > ProjectAccessService.MaxTextBytes) { return new(ProjectReadState.TooLarge); }

        var blob = await RunAsync(root, ["cat-file", "blob", oid], ProjectAccessService.MaxTextBytes, ct).ConfigureAwait(false);
        if (blob.ExitCode != 0) { return new(ProjectReadState.Unavailable); }

        using var stream = new MemoryStream(blob.Bytes, writable: false);
        return await ProjectAccessService.ReadTextAsync(stream, ct).ConfigureAwait(false);
    }

    private async Task<(int ExitCode, string Text, byte[] Bytes)> RunAsync(
        string root, IReadOnlyList<string> arguments, int maxBytes, CancellationToken ct,
        IReadOnlyList<string>? configuration = null, byte[]? input = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        // Git before 2.45 rejects --no-lazy-fetch and ignores this variable, so the empty allowlist also blocks every transport.
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_ALLOW_PROTOCOL"] = string.Empty;
        start.Environment["GIT_PAGER"] = "cat";
        foreach (var argument in new[] { "--no-optional-locks", "--literal-pathspecs", "-c", "core.fsmonitor=false" })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var setting in configuration ?? [])
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(setting);
        }

        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = start };
        process.Start();
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, maxBytes, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 16384, timeout.Token);
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0 && !(arguments[0] == "rev-parse" && process.ExitCode == 1))
            {
                logger.LogWarning("Git {Operation} failed with exit code {ExitCode}: {Error}",
                    arguments[0], process.ExitCode, Encoding.UTF8.GetString(await stderr.ConfigureAwait(false)));
            }

            return (process.ExitCode, Encoding.UTF8.GetString(output), output);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Git read exceeded its 10 second limit.", ex);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > limit) { throw new InvalidDataException("Git output exceeded the read limit."); }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
