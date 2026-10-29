using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Microsoft.Mxc.Sdk.V1;
using Microsoft.Mxc.Sdk.V1.Policy;
using Oscova.Agents.Execution;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Plugins.Mxc;

/// <summary>Composes native policy and owns the private scratch granted to one execution session.</summary>
internal sealed class MxcRuntime : IDisposable
{
    private readonly ExecutionSessionRequest _request;
    private readonly ILogger _logger;
    private readonly string _scratch;
    private readonly FilesystemPolicy _filesystem;
    private readonly Dictionary<string, string> _environment;
    private readonly BlockingCollection<Action>? _launches;
    private readonly Task? _launchThread;

    public MxcRuntime(ExecutionSessionRequest request, string privateRoot, ILogger logger)
    {
        _request = request;
        _logger = logger;
        Directory.CreateDirectory(privateRoot);
        _scratch = Directory.CreateDirectory(Path.Combine(privateRoot, "session-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            _filesystem = CreateFilesystem(request);
            _filesystem.ReadwritePaths.Add(_scratch);
            _environment = request.Environment.Where(item => item.Value is not null)
                .ToDictionary(item => item.Key, item => item.Value!, OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var key in new[] { "HOME", "USERPROFILE", "TEMP", "TMP", "TMPDIR", "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "APPDATA", "LOCALAPPDATA" })
            {
                _environment[key] = _scratch;
            }
            if (OperatingSystem.IsLinux())
            {
                // Bubblewrap ties parent death to the spawning thread, which must outlive every session process.
                _launches = new();
                _launchThread = Task.Factory.StartNew(() =>
                {
                    foreach (var launch in _launches.GetConsumingEnumerable()) { launch(); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static string Shell => OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash";
    public static string ShellCommand => OperatingSystem.IsWindows()
        ? "powershell.exe -NoLogo -NoProfile -NonInteractive -Command -"
        : "/bin/bash --noprofile --norc";

    private static FilesystemPolicy CreateFilesystem(ExecutionSessionRequest request)
    {
        var grants = request.Directories.ToList();
        if (request.SkillsDirectory is { } skills)
        {
            grants.Add(new(skills, "Skills", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly));
        }

        // Native mounts expose aliases independently. Apply grants to both physical and alias paths.
        var mappings = grants.Select(grant => (Grant: grant, Physical: ResolveDirectory(grant.Value))).ToArray();
        foreach (var (grant, physical) in mappings)
        {
            // Alias grants carry permissions, while only the original selects the working directory.
            grants.Add(grant with { Value = physical, IsWorkingDirectory = false });
            foreach (var parent in mappings.Where(parent => PathHelper.IsPathWithinDirectory(physical, parent.Physical)))
            {
                grants.Add(grant with { Value = Path.GetFullPath(Path.Combine(parent.Grant.Value, Path.GetRelativePath(parent.Physical, physical))), IsWorkingDirectory = false });
            }
        }

        var policy = new FilesystemPolicy { ClearPolicyOnExit = true };
        foreach (var grant in grants)
        {
            var path = PathHelper.ResolveAuthorizedHostPath(grant.Value, grants, WorkspacePathAccess.Read);
            PathHelper.TryGetContainingDirectory(path, grants, out var effective);
            var paths = effective!.AccessMode == WorkspaceAccessMode.ReadOnly ? policy.ReadonlyPaths : policy.ReadwritePaths;
            if (!paths.Any(existing => PathHelper.ArePathsEqual(existing, path))) { paths.Add(path); }
        }

        // Tool discovery never grants the helper's writable profile paths or broad home/temp roots.
        foreach (var path in Filesystem.GetAvailableToolsPolicy(request.Environment).ReadonlyPaths)
        {
            var full = Path.GetFullPath(path);
            if (PathHelper.ArePathsEqual(full, Path.GetPathRoot(full)!) ||
                request.Environment.Any(item => item.Key is "HOME" or "USERPROFILE" or "TEMP" or "TMP" &&
                    !string.IsNullOrEmpty(item.Value) && PathHelper.IsPathWithinDirectory(item.Value, full)) ||
                PathHelper.TryGetContainingDirectory(full, grants, out _) ||
                grants.Any(grant => PathHelper.IsPathWithinDirectory(grant.Value, full)))
            {
                continue;
            }
            policy.ReadonlyPaths.Add(full);
        }
        return policy;
    }

    // Resolve linked ancestors before projecting read-only grants onto native mounts.
    private static string ResolveDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(full)!;
        foreach (var part in full[resolved.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(resolved, part));
            resolved = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        return resolved;
    }

    public async Task<MxcProcess> SpawnAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = new ContainerRequest(ShellCommand)
        {
            WorkingDirectory = _request.WorkingDirectory,
            Environment = _environment,
            Filesystem = _filesystem,
            // Windows shells need Win32k during initialization, while clipboard and input remain denied.
            Ui = new() { Disable = !OperatingSystem.IsWindows(), Clipboard = ClipboardPolicy.None, AllowInputInjection = false },
            Network = new()
            {
                Egress = new() { Default = _request.AllowNetwork && !_request.Offline ? NetworkAction.Allow : NetworkAction.Deny },
                Ingress = new() { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny }
            }
        };
        if (OperatingSystem.IsWindows())
        {
            // PowerShell queries ancestors during path resolution without needing their file contents.
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _filesystem.ReadwritePaths.Concat(_filesystem.ReadonlyPaths))
            {
                for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
                {
                    parents.Add(parent.FullName);
                }
            }
            request.Containment = new Containment.ProcessContainer
            {
                Filesystem = new() { EnumeratePaths = parents.ToList() }
            };
        }
        // Native creation cannot be interrupted. Keep ownership until a late process has been disposed.
        MxcProcess Spawn() => MxcContainer.Spawn(request, new() { Telemetry = new() { Enabled = false } });
        Task<MxcProcess> creation;
        if (_launches is null) { creation = Task.Run(Spawn, CancellationToken.None); }
        else
        {
            var completion = new TaskCompletionSource<MxcProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
            _launches.Add(() =>
            {
                try { completion.SetResult(Spawn()); }
                catch (Exception ex) { completion.SetException(ex); }
            }, ct);
            creation = completion.Task;
        }
        try
        {
            var process = await creation.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var warning in process.Warnings) { _logger.LogWarning("MXC launch warning: {Warning}", warning); }
                return process;
            }
            catch { process.Dispose(); throw; }
        }
        catch (OperationCanceledException)
        {
            try { (await creation.ConfigureAwait(false)).Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "MXC creation cleanup failed"); }
            throw;
        }
    }

    public void Dispose()
    {
        _launches?.CompleteAdding();
        _launchThread?.GetAwaiter().GetResult();
        _launches?.Dispose();
        try { Directory.Delete(_scratch, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove MXC scratch directory {Directory}", _scratch);
        }
    }
}
