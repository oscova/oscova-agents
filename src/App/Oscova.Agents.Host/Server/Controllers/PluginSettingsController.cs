using Oscova.Agents.Core.Auth;
using Oscova.Agents.Host.Server.Nodes;
using Oscova.Agents.Core.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Plugins;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Infrastructure.Plugins.Registry;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Plugins;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Manages system-wide plugin activation and package removal.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/plugin-settings")]
public sealed class PluginSettingsController(
    PluginActivationSettings activationSettings,
    PluginStartupState startupState,
    IAgentPluginCatalog pluginCatalog,
    IPluginInstallService pluginInstaller,
    ISystemCredentialService systemCredentials,
    PendingPluginUpdateBootstrapper pendingUpdates,
    ILogger<PluginSettingsController> logger,
    ExecutionPermissionRegistry executions,
    NodePluginDeploymentService nodePlugins) : ControllerBase
{
    private readonly PluginActivationSettings _activationSettings = activationSettings;
    private readonly PluginStartupState _startupState = startupState;
    private readonly IAgentPluginCatalog _pluginCatalog = pluginCatalog;
    private readonly IPluginInstallService _pluginInstaller = pluginInstaller;
    private readonly PendingPluginUpdateBootstrapper _pendingUpdates = pendingUpdates;
    private readonly ILogger<PluginSettingsController> _logger = logger;

    /// <summary>
    /// Lists inspected plugin manifests with current and next-start activation state.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PluginSettingsSummary>> Get()
    {
        try
        {
            await _activationSettings.LoadAsync();
            return Ok(BuildSummary());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin activation settings.");
            return Problem("An unexpected error occurred while loading plugin settings.");
        }
    }

    /// <summary>
    /// Saves the explicit plugin allowlist used on the next process startup.
    /// </summary>
    [HttpPut]
    public async Task<ActionResult<PluginSettingsSummary>> Save([FromBody] UpdatePluginSettingsRequest request)
    {
        if (request.EnabledPluginIds?.Any(string.IsNullOrWhiteSpace) != false)
        {
            return BadRequest("Enabled plugin IDs must not be blank.");
        }

        try
        {
            await _activationSettings.LoadAsync();
            var requestedIds = request.EnabledPluginIds
                .Select(pluginId => pluginId.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var manageableIds = GetManageablePluginIds();
            var unknownIds = requestedIds
                .Except(manageableIds, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (unknownIds.Length > 0)
            {
                return BadRequest("Only installed plugins with valid manifests can be enabled.");
            }

            var pending = _pendingUpdates.GetPendingUpdates();
            foreach (var inspection in AgentPluginLoader.InspectDirectory(PathUtil.PluginsPath))
            {
                if (inspection.Manifest is not { } manifest || !requestedIds.Contains(manifest.Id)) { continue; }

                var effective = pending.FirstOrDefault(update => string.Equals(update.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)) ?? manifest;
                var reason = PluginFrameworkCompatibility.GetIncompatibility(effective.FrameworkVersionRange, PluginFrameworkCompatibility.CurrentVersion);
                if (reason is not null) { return BadRequest(reason); }
            }

            var saved = false;
            await executions.ChangeConfigurationAsync(
                () => _activationSettings.EnabledPluginIds.Except(requestedIds, StringComparer.OrdinalIgnoreCase)
                    .Select(PluginCapability.ConfigurationKey).ToArray(), async () =>
                {
                    _activationSettings.SetEnabledPluginIds(requestedIds);
                    saved = await _activationSettings.TrySaveAsync().ConfigureAwait(false);
                }, confirmed: true, HttpContext.RequestAborted).ConfigureAwait(false);
            if (!saved)
            {
                return Problem("Plugin settings could not be saved.");
            }

            nodePlugins.ActivationChanged();
            return Ok(BuildSummary());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save plugin activation settings.");
            return Problem("An unexpected error occurred while saving plugin settings.");
        }
    }

    /// <summary>
    /// Removes an installed plugin that is inactive now and disabled for the next restart.
    /// </summary>
    [HttpDelete("{pluginId}")]
    public async Task<ActionResult<PluginSettingsSummary>> Uninstall(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return BadRequest("Plugin ID is required.");
        }

        try
        {
            await _activationSettings.LoadAsync();
            var inspection = AgentPluginLoader.InspectDirectory(PathUtil.PluginsPath)
                .FirstOrDefault(inspection =>
                    inspection.Diagnostic is null &&
                    string.Equals(inspection.Manifest?.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            var installedPluginId = inspection?.Manifest?.Id;
            var pluginDirectory = installedPluginId is null
                ? null
                : Path.GetFullPath(Path.Combine(PathUtil.PluginsPath, installedPluginId));
            var inspectedDirectory = inspection is null
                ? null
                : Path.GetDirectoryName(inspection.ManifestPath);
            if (installedPluginId is null ||
                pluginDirectory is null ||
                inspectedDirectory is null ||
                PathHelper.ArePathsEqual(pluginDirectory, PathUtil.PluginsPath) ||
                // Canonical plugin names are case-insensitive identities within the exact plugin root.
                !PathHelper.ArePathsEqual(Path.GetDirectoryName(inspectedDirectory)!, PathUtil.PluginsPath) ||
                !string.Equals(Path.GetFileName(inspectedDirectory), installedPluginId, StringComparison.OrdinalIgnoreCase))
            {
                return NotFound("Installed plugin was not found.");
            }

            if (_startupState.WasEnabled(installedPluginId) || _activationSettings.IsEnabled(installedPluginId))
            {
                return Conflict("Disable the plugin, save, and restart Oscova Agents before uninstalling it.");
            }

            _activationSettings.SetRemoved(installedPluginId, removed: true);
            if (!await _activationSettings.TrySaveAsync())
            {
                return Problem("Plugin settings could not be saved.");
            }

            await systemCredentials.DeletePluginNamespaceAsync(installedPluginId, HttpContext.RequestAborted).ConfigureAwait(false);
            _pluginInstaller.Uninstall(installedPluginId);
            return Ok(BuildSummary());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall plugin {PluginId}.", pluginId);
            return Problem("An unexpected error occurred while uninstalling the plugin.");
        }
    }

    private PluginSettingsSummary BuildSummary()
    {
        var inspections = AgentPluginLoader.InspectDirectory(PathUtil.PluginsPath);
        var loadedPluginsById = _pluginCatalog.Plugins
            .ToDictionary(plugin => plugin.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var entries = inspections
            .Select(inspection => BuildInspectedEntry(inspection, loadedPluginsById))
            .ToList();
        var inspectedIds = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .Select(entry => entry.Id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Keep an already-active plugin visible if its files are removed before the next restart.
        foreach (var loadedPlugin in _pluginCatalog.Plugins.Where(plugin => !inspectedIds.Contains(plugin.Manifest.Id)))
        {
            entries.Add(new PluginSettingsEntry
            {
                Id = loadedPlugin.Manifest.Id,
                Name = loadedPlugin.Manifest.Name,
                Version = loadedPlugin.Manifest.Version,
                HardwareRequirement = GetHardwareRequirement(loadedPlugin.Manifest),
                Assembly = loadedPlugin.Manifest.Assembly,
                EntryType = loadedPlugin.Manifest.EntryType,
                Status = PluginRuntimeStatus.Active,
                CanConfigure = true,
                EnabledAtStartup = _startupState.WasEnabled(loadedPlugin.Manifest.Id),
                LoadOnNextRestart = _activationSettings.IsEnabled(loadedPlugin.Manifest.Id),
                DiagnosticMessage = "Plugin files were not found during the current disk inspection."
            });
        }

        var representedIds = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .Select(entry => entry.Id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An allowlisted plugin must remain removable even when its installed files disappear.
        foreach (var enabledPluginId in _activationSettings.EnabledPluginIds.Where(pluginId => !representedIds.Contains(pluginId)))
        {
            entries.Add(new PluginSettingsEntry
            {
                Id = enabledPluginId,
                Name = enabledPluginId,
                Status = PluginRuntimeStatus.InvalidManifest,
                CanConfigure = true,
                EnabledAtStartup = _startupState.WasEnabled(enabledPluginId),
                LoadOnNextRestart = true,
                DiagnosticMessage = "Enabled plugin installation was not found during the current disk inspection."
            });
        }

        // Pending metadata describes the next process, while current status and activation stay unchanged.
        var pending = _pendingUpdates.GetPendingUpdates();
        foreach (var manifest in pending)
        {
            foreach (var entry in entries.Where(entry => string.Equals(entry.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
            {
                entry.PendingName = manifest.Name;
                entry.PendingVersion = manifest.Version;
                entry.PendingHardwareRequirement = GetHardwareRequirement(manifest);
                entry.CanConfigure = PluginFrameworkCompatibility.GetIncompatibility(manifest.FrameworkVersionRange, PluginFrameworkCompatibility.CurrentVersion) is null;
            }
        }

        var statuses = nodePlugins.GetStatus(string.Empty, isOperator: true);
        foreach (var entry in entries) { entry.Devices = statuses.FirstOrDefault(status => status.PluginId == entry.Id); }

        return new PluginSettingsSummary
        {
            Plugins = [.. entries
                .OrderBy(entry => entry.Name ?? entry.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)],
            RestartRequired = pending.Count > 0 || !_startupState.EnabledPluginIds.SetEquals(_activationSettings.EnabledPluginIds)
        };
    }

    private PluginHardwareRequirement? GetHardwareRequirement(AgentPluginManifest? manifest)
    {
        if (manifest is null) { return null; }

        var isModelProvider = _pluginCatalog.Plugins.Any(plugin =>
            string.Equals(plugin.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase) && plugin.ModelProviders.Count > 0);
        if (manifest.HardwareRequirements is null && !isModelProvider) { return null; }

        // Unknown model metadata remains advisory; plugins without a model declaration have no guidance.
        return manifest.HardwareRequirements?.GetValueOrDefault(AgentPluginLoader.SupportedRuntimeIdentifier) ?? new("unknown");
    }

    private PluginSettingsEntry BuildInspectedEntry(
        AgentPluginInspection inspection,
        IReadOnlyDictionary<string, LoadedAgentPlugin> loadedPluginsById)
    {
        var manifest = inspection.Manifest;
        if (manifest is null || inspection.Diagnostic is not null)
        {
            var isEnabled = manifest is not null && _activationSettings.IsEnabled(manifest.Id);
            return new PluginSettingsEntry
            {
                Id = manifest?.Id,
                Name = manifest?.Name,
                Version = manifest?.Version,
                HardwareRequirement = GetHardwareRequirement(manifest),
                Assembly = manifest?.Assembly,
                EntryType = manifest?.EntryType,
                Status = PluginRuntimeStatus.InvalidManifest,
                CanConfigure = isEnabled,
                EnabledAtStartup = manifest is not null && _startupState.WasEnabled(manifest.Id),
                LoadOnNextRestart = isEnabled,
                DiagnosticMessage = inspection.Diagnostic?.Message
            };
        }

        var enabledAtStartup = _startupState.WasEnabled(manifest.Id);
        var isActive = loadedPluginsById.ContainsKey(manifest.Id);
        var pluginDirectory = Path.GetDirectoryName(inspection.ManifestPath);
        var loadDiagnostics = _pluginCatalog.Diagnostics
            .Where(diagnostic => string.Equals(diagnostic.Path, inspection.ManifestPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(diagnostic.Path, pluginDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(diagnostic => diagnostic.Message)
            .ToArray();

        return new PluginSettingsEntry
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            HardwareRequirement = GetHardwareRequirement(manifest),
            Assembly = manifest.Assembly,
            EntryType = manifest.EntryType,
            Status = isActive
                ? PluginRuntimeStatus.Active
                : enabledAtStartup
                    ? PluginRuntimeStatus.LoadFailed
                    : PluginRuntimeStatus.Inactive,
            CanConfigure = PluginFrameworkCompatibility.GetIncompatibility(manifest.FrameworkVersionRange, PluginFrameworkCompatibility.CurrentVersion) is null,
            EnabledAtStartup = enabledAtStartup,
            LoadOnNextRestart = _activationSettings.IsEnabled(manifest.Id),
            DiagnosticMessage = PluginFrameworkCompatibility.GetIncompatibility(manifest.FrameworkVersionRange, PluginFrameworkCompatibility.CurrentVersion)
                ?? (loadDiagnostics.Length == 0 ? null : string.Join(" ", loadDiagnostics))
        };
    }

    private HashSet<string> GetManageablePluginIds()
    {
        var inspectedIds = AgentPluginLoader.InspectDirectory(PathUtil.PluginsPath)
            .Where(inspection => inspection.Diagnostic is null && inspection.Manifest is not null)
            .Select(inspection => inspection.Manifest!.Id);

        return inspectedIds
            .Concat(_pluginCatalog.Plugins.Select(plugin => plugin.Manifest.Id))
            .Concat(_activationSettings.EnabledPluginIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
