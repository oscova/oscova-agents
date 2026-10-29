using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Oscova.Agents.Core.ApiContracts.Plugins.Registry;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Components.Dialogs.Plugins;

public partial class PluginRegistryBrowserDialog : ComponentBase
{
    [CascadingParameter]
    public DialogInstance DialogInstance { get; set; } = default!;

    private bool _loading;
    private bool _hasSearched;
    private string _query = string.Empty;
    private List<RemotePluginCatalogEntry> _results = [];
    private List<string> _errors = [];
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _metadataErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InstallState> _installs = new(StringComparer.OrdinalIgnoreCase);
    private bool _installedAny;

    private async Task SearchAsync()
    {
        var trimmed = (_query ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            _results = [];
            _errors = [];
            _hasSearched = false;
            return;
        }

        _loading = true;
        _errors = [];
        _hasSearched = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var catalog = await SettingsApiClient.SearchPluginRegistryAsync(trimmed);
            _results = [.. catalog.Entries.OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to search plugin registry for query {Query}.", trimmed);
            _results = [];
            _errors = [ex.Message];
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task OnQueryKeyDownAsync(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Enter", StringComparison.Ordinal))
        {
            await SearchAsync();
        }
    }

    private static string EntryKey(RemotePluginCatalogEntry entry) => entry.Id;

    private static string DisplayTitle(RemotePluginCatalogEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title;

    private bool IsInstalling(string key) =>
        _installs.TryGetValue(key, out var state) && state.InProgress;

    private bool IsInstallDisabled(RemotePluginCatalogEntry entry) =>
        entry.UnavailableReason is not null ||
        _installs.TryGetValue(EntryKey(entry), out var state) && (state.InProgress || state.Success);

    private string? UnavailableText(RemotePluginCatalogEntry entry) => entry.UnavailableReason switch
    {
        null => null,
        RemotePluginCatalogEntry.RequiresNewerOscova => Loc.GetOrDefault(SettingsTextKeys.PluginsFindOnlineRequiresNewerOscova, "Requires a newer Oscova"),
        RemotePluginCatalogEntry.PrereleaseOnly => Loc.GetOrDefault(SettingsTextKeys.PluginsFindOnlinePrereleaseOnly, "Prerelease only"),
        _ => Loc.GetOrDefault(SettingsTextKeys.PluginsFindOnlineIncompatible, "Not compatible with this Oscova")
    };

    private async Task ToggleAsync(RemotePluginCatalogEntry entry)
    {
        var key = EntryKey(entry);
        if (_expanded.Contains(key))
        {
            _expanded.Remove(key);
            return;
        }

        _expanded.Add(key);
        _metadataErrors.Remove(key);
        _loadingMetadata.Add(key);
        await InvokeAsync(StateHasChanged);

        try
        {
            var metadata = await SettingsApiClient.GetPluginRegistryMetadataAsync(entry.Id);
            if (metadata is not null)
            {
                ApplyMetadata(entry, metadata);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to fetch plugin metadata for package {PackageId}.", entry.Id);
            _metadataErrors.Add(key);
        }
        finally
        {
            _loadingMetadata.Remove(key);
        }
    }

    private static void ApplyMetadata(RemotePluginCatalogEntry target, RemotePluginCatalogEntry metadata)
    {
        target.Title = metadata.Title;
        target.Version = metadata.Version;
        target.Authors = metadata.Authors;
        target.Description = metadata.Description;
        target.ProjectUrl = metadata.ProjectUrl;
        target.LicenseExpression = metadata.LicenseExpression;
        target.IconUrl = metadata.IconUrl;
        target.DownloadCount = metadata.DownloadCount;
        target.IsVerified = metadata.IsVerified;
        target.UnavailableReason = metadata.UnavailableReason;
    }

    private async Task InstallAsync(RemotePluginCatalogEntry entry, PluginInstallFailure? replacement = null)
    {
        var key = EntryKey(entry);
        if (IsInstalling(key)) { return; }

        var state = new InstallState();
        _installs[key] = state;
        StateHasChanged();

        try
        {
            var error = await SettingsApiClient.InstallRemotePluginAsync(new InstallRemotePluginRequest
            {
                PackageId = entry.Id,
                ReplacePluginId = replacement?.PluginId,
                ReplacePackageId = replacement is null ? null : replacement.PackageId ?? string.Empty
            });

            if (error is not null)
            {
                state.Replacement = error.Code == PluginInstallFailure.ReplacementRequired &&
                    !string.IsNullOrWhiteSpace(error.PluginId) ? error : null;
                state.Fail(error.Message);
                return;
            }

            state.Complete();
            _installedAny = true;
            ToastService.Show(
                Loc.GetOrDefault(SettingsTextKeys.PluginsFindOnlineInstalledToast, "Plugin package is ready. Confirm Load On Next Restart, then restart."),
                ToastType.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to install plugin package {PackageId}.", entry.Id);
            state.Fail(ex.Message);
        }
    }

    private async Task ReplaceAsync(RemotePluginCatalogEntry entry, PluginInstallFailure replacement)
    {
        var current = replacement.PackageId ?? replacement.PluginId!;
        var title = FormatSettingsText(SettingsTextKeys.PluginsReplaceConfirmTitle,
            "Replace {0} with {1}?", current, entry.Id);
        var result = await DialogService.ShowAsync<ConfirmDialog>(title,
            Loc.GetOrDefault(SettingsTextKeys.PluginsReplaceConfirmMessage,
                "This replacement keeps the plugin's identity, settings, and credentials."));
        if (result == DialogResult.Ok) { await InstallAsync(entry, replacement); }
    }

    private Task Close()
    {
        DialogInstance.Close(_installedAny ? DialogResult.Ok : DialogResult.Cancel);
        return Task.CompletedTask;
    }

    private string FormatSettingsText(string key, string fallback, params object?[] args)
    {
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.GetOrDefault(key, fallback), args);
    }

    private sealed class InstallState
    {
        public bool InProgress { get; private set; } = true;
        public bool Success { get; private set; }
        public string? Error { get; private set; }
        public PluginInstallFailure? Replacement { get; set; }

        public void Complete()
        {
            InProgress = false;
            Success = true;
        }

        public void Fail(string message)
        {
            InProgress = false;
            Error = message;
        }
    }
}
