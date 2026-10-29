using Microsoft.Extensions.Logging;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Plugins;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Plugins.Registry;

/// <summary>Promotes validated plugin updates before plugin assemblies are loaded.</summary>
public sealed class PendingPluginUpdateBootstrapper
{
    // Inside the plugins root, so every promotion is a same-mount rename even when only that folder is a volume.
    private const string PendingRootName = ".pending-updates";
    private const string BackupRootName = ".update-backups";

    private readonly ILogger<PendingPluginUpdateBootstrapper> _logger;
    private readonly string _pluginsRoot;
    private readonly string _pendingRoot;
    private readonly string _backupRoot;

    public PendingPluginUpdateBootstrapper(ILogger<PendingPluginUpdateBootstrapper> logger)
        : this(logger, PathUtil.PluginsPath)
    {
    }

    public PendingPluginUpdateBootstrapper(
        ILogger<PendingPluginUpdateBootstrapper> logger,
        string pluginsRoot)
    {
        _logger = logger;
        _pluginsRoot = Path.GetFullPath(pluginsRoot);
        _pendingRoot = Path.Combine(_pluginsRoot, PendingRootName);
        _backupRoot = Path.Combine(_pluginsRoot, BackupRootName);
    }

    /// <summary>Reconciles pending, canonical, and backup plugin directories into a discoverable install.</summary>
    public void ApplyPendingUpdates()
    {
        var pluginIds = EnumerateChildNames(_pendingRoot)
            .Concat(EnumerateChildNames(_backupRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var pluginId in pluginIds)
        {
            ApplyPendingUpdate(pluginId);
        }
    }

    /// <summary>Removes empty pending and backup roots so a staged environment keeps its recorded inventory.</summary>
    public void RemoveEmptyRoots()
    {
        foreach (var root in new[] { _pendingRoot, _backupRoot })
        {
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) { Directory.Delete(root); }
        }
    }

    /// <summary>Returns validated pending manifests for the next-restart settings display without loading code.</summary>
    public IReadOnlyList<AgentPluginManifest> GetPendingUpdates()
    {
        var manifests = new List<AgentPluginManifest>();
        foreach (var pluginId in EnumerateChildNames(_pendingRoot))
        {
            var directory = ResolveChildDirectory(_pendingRoot, pluginId);
            if (IsValidPendingUpdate(directory, pluginId, out var manifest, out _))
                manifests.Add(manifest!);
        }

        return manifests;
    }

    internal static string ResolvePendingDirectory(string pluginsRoot, string pluginId)
    {
        return ResolveChildDirectory(Path.Combine(Path.GetFullPath(pluginsRoot), PendingRootName), pluginId);
    }

    internal static string ResolveBackupDirectory(string pluginsRoot, string pluginId)
    {
        return ResolveChildDirectory(Path.Combine(Path.GetFullPath(pluginsRoot), BackupRootName), pluginId);
    }

    private void ApplyPendingUpdate(string pluginId)
    {
        var canonicalDirectory = ResolveChildDirectory(_pluginsRoot, pluginId);
        var pendingDirectory = ResolveChildDirectory(_pendingRoot, pluginId);
        var backupDirectory = ResolveChildDirectory(_backupRoot, pluginId);
        var hasCanonical = Directory.Exists(canonicalDirectory);
        var hasPending = Directory.Exists(pendingDirectory);
        var hasBackup = Directory.Exists(backupDirectory);

        if (hasCanonical && hasPending && hasBackup)
        {
            DeleteBackupBestEffort(backupDirectory, pluginId);
            hasBackup = Directory.Exists(backupDirectory);
            if (hasBackup) return;
        }

        if (hasPending && !IsValidPendingUpdate(pendingDirectory, pluginId, out _, out var validationError))
        {
            DeleteInvalidPendingDirectory(pendingDirectory, pluginId, validationError!);
            RecoverWithoutPending(canonicalDirectory, backupDirectory, pluginId);
            return;
        }

        if (!hasPending)
        {
            RecoverWithoutPending(canonicalDirectory, backupDirectory, pluginId);
            return;
        }

        Directory.CreateDirectory(_pluginsRoot);
        Directory.CreateDirectory(_backupRoot);

        try
        {
            if (hasCanonical)
            {
                Directory.Move(canonicalDirectory, backupDirectory);
                hasBackup = true;
            }

            Directory.Move(pendingDirectory, canonicalDirectory);
        }
        catch (Exception promotionError)
        {
            if (hasBackup && !Directory.Exists(canonicalDirectory))
            {
                try
                {
                    Directory.Move(backupDirectory, canonicalDirectory);
                }
                catch (Exception restoreError)
                {
                    throw new AggregateException(
                        $"Plugin update '{pluginId}' failed and its prior install could not be restored.",
                        promotionError,
                        restoreError);
                }
            }

            _logger.LogWarning(
                promotionError,
                "Plugin update {PluginId} could not be promoted. Installed plugin state was preserved and the update will be retried on startup.",
                pluginId);
            return;
        }

        DeleteBackupBestEffort(backupDirectory, pluginId);
    }

    private void RecoverWithoutPending(string canonicalDirectory, string backupDirectory, string pluginId)
    {
        if (Directory.Exists(canonicalDirectory))
        {
            DeleteBackupBestEffort(backupDirectory, pluginId);
            return;
        }

        if (Directory.Exists(backupDirectory))
        {
            Directory.CreateDirectory(_pluginsRoot);
            Directory.Move(backupDirectory, canonicalDirectory);
        }
    }

    private static bool IsValidPendingUpdate(
        string pendingDirectory,
        string expectedPluginId,
        out AgentPluginManifest? manifest,
        out string? error)
    {
        manifest = null;
        var inspections = AgentPluginLoader.InspectDirectory(pendingDirectory);
        if (inspections.Count != 1 ||
            inspections[0].Manifest is null ||
            inspections[0].Diagnostic is not null)
        {
            error = inspections.FirstOrDefault()?.Diagnostic?.Message ??
                "The pending directory did not contain exactly one valid plugin manifest.";
            return false;
        }

        manifest = inspections[0].Manifest!;
        if (!string.Equals(manifest.Id, expectedPluginId, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Manifest id '{manifest.Id}' did not match pending directory '{expectedPluginId}'.";
            return false;
        }

        error = null;
        return true;
    }

    private void DeleteInvalidPendingDirectory(string pendingDirectory, string pluginId, string validationError)
    {
        try
        {
            Directory.Delete(pendingDirectory, recursive: true);
            _logger.LogWarning(
                "Invalid pending update for plugin {PluginId} was removed: {ValidationError}",
                pluginId,
                validationError);
        }
        catch (Exception deleteError)
        {
            throw new InvalidOperationException(
                $"Invalid pending update for plugin '{pluginId}' could not be removed.",
                deleteError);
        }
    }

    private void DeleteBackupBestEffort(string backupDirectory, string pluginId)
    {
        if (!Directory.Exists(backupDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(backupDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Plugin update {PluginId} backup directory could not be removed.",
                pluginId);
        }
    }

    private static IEnumerable<string> EnumerateChildNames(string root)
    {
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !string.IsNullOrWhiteSpace(name))
            : [];
    }

    private static string ResolveChildDirectory(string root, string pluginId)
    {
        var directory = Path.GetFullPath(Path.Combine(root, pluginId));
        if (!PathHelper.IsPathWithinDirectory(directory, root))
        {
            throw new ArgumentException("Plugin id must stay within its update directory.", nameof(pluginId));
        }

        return directory;
    }
}
