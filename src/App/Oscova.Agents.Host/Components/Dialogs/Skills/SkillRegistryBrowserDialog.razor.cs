using Microsoft.AspNetCore.Components.Routing;
using System.Globalization;
using Oscova.Agents.Core.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Oscova.Agents.Core.ApiContracts.Skills.Registry;
using Oscova.Agents.Core.Skills.Registry;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Components.Dialogs.Skills;

public partial class SkillRegistryBrowserDialog : ComponentBase, IDisposable
{
    [CascadingParameter]
    public DialogInstance DialogInstance { get; set; } = default!;

    private readonly CancellationTokenSource _catalogCancellation = new();
    private bool _disposed;

    private bool _loading = true;
    private string _query = string.Empty;
    private List<RemoteSkillCatalogEntry> _all = [];
    private List<string> _errors = [];
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingDescriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _descriptionErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _installedSkillNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InstallState> _installs = new(StringComparer.OrdinalIgnoreCase);
    private bool _installedAny;
    private readonly List<SourcePreparation> _preparations = [];
    private bool _installedLoaded;
    private string _dialogPath = string.Empty;
    private int FinishedSources => _preparations.Count(source => source.Finished);
    private double? OverallPercent => _preparations.Count == 0 ? null : FinishedSources * 100d / _preparations.Count;
    private string PreparationSummary => string.Format(CultureInfo.CurrentCulture,
        Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationSummary, "Skill sources: {0} of {1} finished."),
        FinishedSources, _preparations.Count);

    private IReadOnlyList<RemoteSkillCatalogEntry> Filtered =>
        string.IsNullOrWhiteSpace(_query)
            ? _all
            : _all.Where(e => e.SkillName.Contains(_query, StringComparison.OrdinalIgnoreCase)).ToList();

    protected override async Task OnInitializedAsync()
    {
        var cancellationToken = _catalogCancellation.Token;
        try
        {
            var authentication = await AuthState.GetAuthenticationStateAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (authentication.User.Identity?.IsAuthenticated != true)
            {
                await Close();
                return;
            }

            _dialogPath = Navigation.ToAbsoluteUri(Navigation.Uri).AbsolutePath;
            Navigation.LocationChanged += OnLocationChanged;
            // Discovery uses the configured snapshot, source editing retains its admin-only API.
            _preparations.AddRange(RegistrySettings.CreateSnapshot().Sources.Select(source => new SourcePreparation(source)));
            var installed = LoadInstalledSkillsAsync(cancellationToken);
            var catalogs = _preparations.Select(source => LoadSourceAsync(source, cancellationToken)).ToArray();
            await Task.WhenAll(catalogs);
            await UpdateCatalogUiAsync(() => _loading = false, cancellationToken);
            await installed;
        }
        catch (OperationCanceledException) when (_catalogCancellation.IsCancellationRequested)
        {
            // Closing the dialog cancels catalog preparation without showing an error.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load skill registry catalog.");
            await UpdateCatalogUiAsync(() => _errors.Add(ex.Message), cancellationToken);
        }
        finally
        {
            await UpdateCatalogUiAsync(() => _loading = false, cancellationToken);
        }
    }

    /// <summary>Publishes one completed source without waiting for the other configured sources.</summary>
    private async Task LoadSourceAsync(SourcePreparation source, CancellationToken cancellationToken)
    {
        try
        {
            var catalog = await Registry.EnumerateSkillsAsync(source.Source, cancellationToken,
                progress => _ = UpdateCatalogUiAsync(() =>
                {
                    // A queued native callback cannot overwrite an already published source outcome.
                    if (!source.Finished) { source.Progress = progress; }
                }, cancellationToken));
            await UpdateCatalogUiAsync(() =>
            {
                source.Finished = true;
                source.SkillCount = catalog.Entries.Count;
                _all.AddRange(catalog.Entries);
                _all = [.. _all.OrderBy(entry => entry.SkillName, StringComparer.OrdinalIgnoreCase)];
                _errors.AddRange(catalog.Errors);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to enumerate skill source {Source} on {Branch}.", source.Source.DisplayName, source.Source.Branch);
            await UpdateCatalogUiAsync(() =>
            {
                source.Finished = true;
                source.Failed = true;
                _errors.Add($"{source.Identity}: {ex.Message}");
            }, cancellationToken);
        }
    }

    /// <summary>Loads installation state independently of repository preparation.</summary>
    private async Task LoadInstalledSkillsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var installed = await SettingsApiClient.GetInstalledSkillsAsync(cancellationToken);
            await UpdateCatalogUiAsync(() =>
            {
                foreach (var skill in installed)
                {
                    if (!string.IsNullOrWhiteSpace(skill.Name)) { _installedSkillNames.Add(skill.Name); }
                }

                _installedLoaded = true;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load installed skills for discovery.");
            await UpdateCatalogUiAsync(() => _errors.Add(ex.Message), cancellationToken);
        }
    }

    /// <summary>Applies catalog updates on the renderer and ignores callbacks after dialog cancellation.</summary>
    private async Task UpdateCatalogUiAsync(Action update, CancellationToken cancellationToken)
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested) { return; }

                update();
                StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (_disposed || cancellationToken.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException) when (_disposed || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update skill discovery progress.");
        }
    }

    /// <summary>Cancels this dialog when navigation leaves its screen, retaining same-screen help changes.</summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (_disposed || _catalogCancellation.IsCancellationRequested ||
            string.Equals(_dialogPath, Navigation.ToAbsoluteUri(args.Location).AbsolutePath, StringComparison.OrdinalIgnoreCase)) { return; }

        _ = UpdateCatalogUiAsync(() => _ = Close(), _catalogCancellation.Token);
    }

    private static string EntryKey(RemoteSkillCatalogEntry entry) =>
        $"{entry.Source.Owner}/{entry.Source.Repo}@{entry.Source.Branch}::{entry.SkillPath}";

    private bool IsInstalling(string key) =>
        _installs.TryGetValue(key, out var state) && state.InProgress;

    private bool IsInstalled(RemoteSkillCatalogEntry entry) =>
        _installedSkillNames.Contains(entry.SkillName);

    private bool IsInstallComplete(RemoteSkillCatalogEntry entry, string key) =>
        IsInstalled(entry) || (_installs.TryGetValue(key, out var state) && state.Success);

    private bool IsInstallDisabled(RemoteSkillCatalogEntry entry, string key) =>
        !_installedLoaded || IsInstallComplete(entry, key) || IsInstalling(key);

    /// <summary>Formats the current phase or completed catalog count for one source.</summary>
    private string PreparationStatus(SourcePreparation source)
    {
        if (source.Failed)
        {
            return Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationFailed, "Failed");
        }

        if (source.Finished)
        {
            var template = source.Progress.Stage == SkillRegistryPreparationStage.Cached
                ? Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationReadyCached, "Ready: {0} skills (cached)")
                : Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationReady, "Ready: {0} skills");
            return string.Format(CultureInfo.CurrentCulture, template, source.SkillCount);
        }

        return source.Progress.Stage switch
        {
            SkillRegistryPreparationStage.CheckingCache => Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationCheckingCache, "Checking cache"),
            SkillRegistryPreparationStage.Waiting => Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationWaiting, "Waiting to prepare"),
            SkillRegistryPreparationStage.Connecting => Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationConnecting, "Connecting"),
            SkillRegistryPreparationStage.ReadingMetadata => Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationReadingMetadata, "Reading repository metadata"),
            SkillRegistryPreparationStage.Downloading => Loc.GetOrDefault(SettingsTextKeys.SkillsInstallStageDownloading, "Downloading"),
            SkillRegistryPreparationStage.Indexing => Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationIndexing, "Indexing download"),
            SkillRegistryPreparationStage.PreparingFiles => Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationPreparingFiles, "Preparing files"),
            SkillRegistryPreparationStage.Scanning => Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationScanning, "Scanning skills"),
            SkillRegistryPreparationStage.Cached => Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationCached, "Cached"),
            _ => CommonLoc.GetOrDefault(CommonTextKeys.Loading, "Loading...")
        };
    }

    private string ReceivedBytesLabel(SourcePreparation source) => string.Format(CultureInfo.CurrentCulture,
        Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlinePreparationReceivedBytes, "{0} received"),
        ByteSize.Format(source.Progress.ReceivedBytes));

    /// <summary>Keeps one configured source's transient progress and final catalog outcome.</summary>
    private sealed class SourcePreparation(SkillRegistrySource source)
    {
        public SkillRegistrySource Source { get; } = source;
        public string Identity => $"{Source.DisplayName}@{Source.Branch}";
        public SkillRegistryPreparationProgress Progress { get; set; } = new(SkillRegistryPreparationStage.CheckingCache);
        public bool Finished { get; set; }
        public bool Failed { get; set; }
        public int SkillCount { get; set; }
    }

    private string InstallButtonLabel(RemoteSkillCatalogEntry entry, string key)
    {
        if (IsInstallComplete(entry, key))
        {
            return Loc.GetOrDefault(SettingsTextKeys.SkillsInstalledBadge, "Installed");
        }

        return _installs.TryGetValue(key, out var install) && !string.IsNullOrEmpty(install.Error)
            ? Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlineRetryButton, "Retry")
            : Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlineInstallButton, "Install");
    }

    private async Task ToggleAsync(RemoteSkillCatalogEntry entry)
    {
        var key = EntryKey(entry);
        if (_expanded.Contains(key))
        {
            _expanded.Remove(key);
            return;
        }

        _expanded.Add(key);
        _descriptionErrors.Remove(key);
        if (entry.Description is null)
        {
            _loadingDescriptions.Add(key);
            await InvokeAsync(StateHasChanged);
            try
            {
                entry.Description = await SettingsApiClient.GetSkillRegistryDescriptionAsync(entry.Source, entry.SkillPath);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to fetch skill description for {Path}.", entry.SkillPath);
                _descriptionErrors.Add(key);
            }
            finally
            {
                _loadingDescriptions.Remove(key);
            }
        }
    }

    private async Task InstallAsync(RemoteSkillCatalogEntry entry)
    {
        var key = EntryKey(entry);
        var state = new InstallState(Loc);
        _installs[key] = state;
        state.Begin();
        StateHasChanged();

        try
        {
            var (result, error) = await SettingsApiClient.InstallRemoteSkillAsync(new InstallRemoteSkillRequest
            {
                Owner = entry.Source.Owner,
                Repo = entry.Source.Repo,
                Branch = entry.Source.Branch,
                SkillPath = entry.SkillPath
            });

            if (error is not null)
            {
                state.Fail(error);
            }
            else
            {
                state.Complete();
                _installedAny = true;
                _installedSkillNames.Add(string.IsNullOrWhiteSpace(result?.Name) ? entry.SkillName : result.Name);
                ToastService.Show(
                    Loc.GetOrDefault(SettingsTextKeys.SkillsFindOnlineInstalledToast, "Skill installed."),
                    ToastType.Success);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to install skill {Path}.", entry.SkillPath);
            state.Fail(ex.Message);
        }
    }

    private static string StepIcon(InstallStep step) =>
        step.Done ? "lucide:check-circle-2"
        : step.Active ? "lucide:loader"
        : "lucide:circle";

    // Finish accepted installs before returning the result that refreshes the parent list.
    private bool CanClose => !_installs.Values.Any(state => state.InProgress);

    private Task Close()
    {
        if (!CanClose) { return Task.CompletedTask; }

        _catalogCancellation.Cancel();
        DialogInstance.Close(_installedAny ? DialogResult.Ok : DialogResult.Cancel);
        return Task.CompletedTask;
    }

    private sealed class InstallState
    {
        private readonly IStringLocalizer<SettingsText> _loc;
        private readonly List<InstallStep> _steps = [];
        public IReadOnlyList<InstallStep> Steps => _steps;
        public bool InProgress { get; private set; }
        public bool Success { get; private set; }
        public string? Error { get; private set; }

        public InstallState(IStringLocalizer<SettingsText> loc)
        {
            _loc = loc;
            _steps.Add(new(_loc.GetOrDefault(SettingsTextKeys.SkillsInstallStageResolving, "Resolving files")));
            _steps.Add(new(_loc.GetOrDefault(SettingsTextKeys.SkillsInstallStageDownloading, "Downloading")));
            _steps.Add(new(_loc.GetOrDefault(SettingsTextKeys.SkillsInstallStageImporting, "Importing")));
            _steps.Add(new(_loc.GetOrDefault(SettingsTextKeys.SkillsInstallStageInstalled, "Installed")));
        }

        public void Begin()
        {
            InProgress = true;
            Success = false;
            Error = null;
            foreach (var step in _steps)
            {
                step.Active = false;
                step.Done = false;
            }

            _steps[0].Active = true;
        }

        public void Complete()
        {
            foreach (var step in _steps)
            {
                step.Active = false;
                step.Done = true;
            }

            InProgress = false;
            Success = true;
        }

        public void Fail(string message)
        {
            InProgress = false;
            Success = false;
            Error = message;
            foreach (var step in _steps)
            {
                step.Active = false;
            }
        }
    }

    private sealed class InstallStep(string label)
    {
        public string Label { get; } = label;
        public bool Active { get; set; }
        public bool Done { get; set; }
    }

    /// <summary>Cancels pending catalog reads when the dialog leaves the UI.</summary>
    public void Dispose()
    {
        _disposed = true;
        Navigation.LocationChanged -= OnLocationChanged;
        _catalogCancellation.Cancel();
        _catalogCancellation.Dispose();
    }
}
