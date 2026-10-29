using System.Net;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Oscova.Agents.Conversations;
using Oscova.Agents.Browsing;
using Microsoft.JSInterop;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Llm;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Host.Services.UI;
using Oscova.Agents.Host.Components.Settings;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.Realtime;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Host.Components.Dialogs;
using Oscova.Agents.Host.Components.Dialogs.Chat;
using Oscova.Agents.Host.Components.Chat;

namespace Oscova.Agents.Host.Components.Pages;

public partial class DashboardPage : ComponentBase, IDisposable
{
    [Inject] private SettingsPageRegistry SettingsRegistry { get; set; } = default!;

    private const string DashboardRevealOverlayId = "dashboard-reveal-overlay";

    [Inject] private IToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<DashboardPage> Logger { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ManagementApiClient ManagementApiClient { get; set; } = default!;
    [Inject] private ChatApiClient ChatApiClient { get; set; } = default!;
    [Inject] private ProjectsApiClient ProjectsApiClient { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private SystemHubClient SystemHubClient { get; set; } = default!;
    [Inject] private UserServiceScope UserScope { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ChatDraftState Drafts { get; set; } = default!;

    private bool _isServiceRunning;
    private bool _isCheckingService = true;
    private string _serviceStatusMessage = string.Empty;
    // Cancel in-flight startup work when the circuit/page is torn down during refresh or navigation.
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CultureInfo _renderCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _renderUiCulture = CultureInfo.CurrentUICulture;
    private ElementReference _dashboardRoot;
    private IJSObjectReference? _dashboardResizeModule;
    private List<ChatThread> _threads = [];
    private IReadOnlyList<ProjectDefinition> _projects = [];
    private string? _selectedProjectId;
    private long _fileRefreshVersion;
    private ProjectDefinition? CurrentProject => _projects.FirstOrDefault(project => project.Id == _currentChatThread.ProjectId);
    private ChatThread _currentChatThread = CreateUninitializedThread();
    private ChatThreadViewResponse? _currentThreadView;
    private (ChatThread Thread, ChatThreadViewResponse? View) _conversationBeforeLoading;
    private bool _isConversationLoading;
    private AgentConfiguration _agentConfiguration = new();
    private ModelProviderConfiguration _modelProviderConfiguration = new();
    private string? _selectedAgentId;
    private Guid? _selectedRunModelProviderId;
    private string? _redirectPath;
    private string? _requestedThreadId;
    private long _conversationSelectionVersion;
    private long _serviceProbeVersion;
    private ActivityPanelView _activityPanelView = ActivityPanelView.Collapsed;
    private bool _restoreWorkspaceFocus;
    private string? _deletingThreadId;
    private DetailsSidePanel? _workspace;
    private ChatPanel? _chatPanel;

    private async Task AddFileToChatAsync(WorkspaceFileReference file)
    {
        if (_chatPanel is null) { return; }

        await _chatPanel.AddFileAsync(file);
        await CollapseActivityAsync();
    }
    private Task<bool> ConfirmWorkspaceAsync() => _workspace?.ConfirmScopeAsync() ?? Task.FromResult(true);

    private AgentDefinition MainAgent => _agentConfiguration.MainAgent;
    private IReadOnlyList<AgentDefinition> CustomAgents => _agentConfiguration.CustomAgents;
    private IReadOnlyList<ModelProviderConfig> ModelProviders => [.. ChatModelProviders];
    private Guid? SelectedModelProviderId => _selectedRunModelProviderId;
    private Guid? DefaultModelProviderId =>
        (_modelProviderConfiguration.SelectedChatModelProviderId ?? _modelProviderConfiguration.OperatorDefaultChatModelProviderId) is { } providerId &&
        ChatModelProviders.Any(provider => provider.Id == providerId)
            ? providerId
            : null;
    private bool IsActivityOpen => _activityPanelView != ActivityPanelView.Collapsed;
    private bool IsActivityWide => _activityPanelView == ActivityPanelView.Wide;
    private bool CanChangeIncognitoMode =>
        _currentChatThread.DisplayMessages.Count == 0 &&
        !_threads.Any(thread => string.Equals(thread.Id, _currentChatThread.Id, StringComparison.Ordinal));
    private string DashboardLayoutClass => _activityPanelView switch
    {
        ActivityPanelView.Standard => "activity-layout-standard",
        ActivityPanelView.Wide => "activity-layout-wide",
        _ => "activity-layout-collapsed"
    };

    [Inject] private NodesApiClient NodesApiClient { get; set; } = default!;
    [Inject] private Oscova.Agents.Host.Server.Nodes.NodeBroker NodeBroker { get; set; } = default!;
    private Oscova.Agents.Core.ApiContracts.Nodes.NodeCatalog _nodeCatalog = new([], [], false);
    private IReadOnlySet<string> AvailableProjectIds => _projects.Where(_nodeCatalog.IsProjectAvailable)
        .Select(project => project.Id).ToHashSet(StringComparer.Ordinal);

    private async Task RefreshNodeCatalogAsync()
    {
        try { _nodeCatalog = await NodesApiClient.GetAsync(_disposeCts.Token); }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not refresh project device availability.");
            _nodeCatalog = new([], [], false);
        }

        if (!_disposeCts.IsCancellationRequested) { StateHasChanged(); }
    }

    private void OnNodeStateChanged(string userId)
    {
        if (userId == UserScope.UserId && !_disposeCts.IsCancellationRequested)
        {
            _ = InvokeNotificationAsync(RefreshNodeCatalogAsync);
        }
    }

    private Task InvokeNotificationAsync(Func<Task> callback) => InvokeAsync(async () =>
    {
        // Background publishers can run under another request's culture.
        CultureInfo.CurrentCulture = _renderCulture;
        CultureInfo.CurrentUICulture = _renderUiCulture;
        await callback();
    });

    protected override async Task OnInitializedAsync()
    {
        var cancellationToken = _disposeCts.Token;

        try
        {
            ApplyNavigationStateFromQuery();
            NavigationManager.LocationChanged += OnLocationChanged;
            if (!string.IsNullOrWhiteSpace(_redirectPath))
            {
                return;
            }

            SystemHubClient.OnThreadChangedAsync = OnThreadChangedAsync;
            UserScope.AppService.StateChanged += OnAppServiceStateChanged;
            if (UserScope.BrowserWorkspace is { } browser) { browser.Changed += OnBrowserChanged; }

            NodeBroker.StateChanged += OnNodeStateChanged;
            await RefreshNodeCatalogAsync();

            UserScope.ProjectsSettings.Changed += OnProjectsChanged;

            await LoadChatConfigurationAsync(cancellationToken);
            _projects = await ProjectsApiClient.GetProjectsAsync(cancellationToken);
            await LoadThreadsAsync(cancellationToken);
            await ApplyRequestedThreadAsync(cancellationToken);
            await EnsureCurrentThreadAsync(cancellationToken);

            // Run the initial service status check here (not in OnAfterRenderAsync) so it
            // observes the populated _modelProviderConfiguration.
            await CheckServiceStatusCoreAsync(cancellationToken);

            await base.OnInitializedAsync();
        }
        // A refresh can dispose the component while startup API calls are still running.
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        // Treat externally canceled startup work as expected so it does not surface as an app error.
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, ToastType.Error);
            Logger.LogError(ex, "There was a problem initializing the AI Assistant.");
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_restoreWorkspaceFocus && _dashboardResizeModule is not null)
        {
            _restoreWorkspaceFocus = false;
            await _dashboardResizeModule.InvokeVoidAsync("focusWorkspaceToggle", _dashboardRoot);
        }

        if (!firstRender)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_redirectPath))
            {
                NavigationManager.NavigateTo(_redirectPath, replace: true);
                return;
            }

            await SystemHubClient.EnsureConnectedAsync(_disposeCts.Token);

            try
            {
                _dashboardResizeModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/dashboardPanelResize.js");
                await _dashboardResizeModule.InvokeVoidAsync("attachDashboardPanelResize", _dashboardRoot);
            }
            catch (JSException ex)
            {
                Logger.LogWarning(ex, "Dashboard panel resizing is unavailable; using default widths.");
            }
        }
        // Ignore hub-connect cancellation when the first render is interrupted by disposal.
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, ToastType.Error);
            Logger.LogError(ex, "Failed to initialize dashboard page.");
        }
    }

    private async Task LoadChatConfigurationAsync(CancellationToken cancellationToken = default)
    {
        _agentConfiguration = await ManagementApiClient.GetAgentConfigurationAsync(cancellationToken);
        _modelProviderConfiguration = await ManagementApiClient.GetModelProviderConfigurationAsync(cancellationToken);

        if (!_selectedRunModelProviderId.HasValue ||
            !ChatModelProviders.Any(provider => provider.Id == _selectedRunModelProviderId.Value))
        {
            _selectedRunModelProviderId = DefaultModelProviderId;
        }
    }

    private async Task LoadThreadsAsync(CancellationToken cancellationToken = default)
    {
        var persistedThreads = await ChatApiClient.GetThreadsAsync(cancellationToken);
        var ephemeralThreads = await ChatApiClient.GetEphemeralThreadsAsync(cancellationToken);

        // Both lifetimes share one visible history while remaining separate backend stores.
        _threads = [.. persistedThreads, .. ephemeralThreads];
        var availableIds = _threads.Select(thread => thread.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in Drafts.LoadedThreadViews.Keys.Where(id => !availableIds.Contains(id)).ToArray())
        {
            Drafts.LoadedThreadViews.Remove(id);
        }

        if (_currentChatThread.BranchOrigin is { } origin)
        {
            _currentChatThread.SourceThreadAvailable = _threads.Any(thread =>
                string.Equals(thread.Id, origin.ParentThreadId, StringComparison.OrdinalIgnoreCase));
        }

        _threads.Sort(static (left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
    }

    private async Task EnsureCurrentThreadAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_currentChatThread.Id))
        {
            return;
        }

        // A chat the user selects or starts while this loads must not be replaced by the startup choice.
        var selectionVersion = _conversationSelectionVersion;

        // A draft resumes its own chat even after the recent-chat window expires.
        var draft = Drafts.ActiveThread;
        if (draft is not null && (draft.DisplayMessages.Count > 0 ||
            _threads.Any(thread => string.Equals(thread.Id, draft.Id, StringComparison.Ordinal))))
        {
            // Reload saved chats so current history and access checks still apply.
            Drafts.LoadedThreadViews.TryGetValue(draft.Id, out var loaded);
            var view = Drafts.GetText(draft.Id).Length > 0
                ? await ChatApiClient.GetThreadViewAsync(draft.Id, cancellationToken,
                    firstMessageId: loaded?.Thread.DisplayMessages.FirstOrDefault()?.MessageId) : null;
            if (selectionVersion != _conversationSelectionVersion) { return; }

            _currentThreadView = view;
            draft = view?.Thread;
        }

        if (draft is not null)
        {
            _currentChatThread = draft;
            ApplyCurrentThreadAgentDefaults();
            await ApplyThreadModelChoiceAsync(_currentThreadView, cancellationToken);
            return;
        }

        var recentView = await ChatApiClient.GetRecentCurrentThreadViewAsync(cancellationToken);
        if (selectionVersion != _conversationSelectionVersion) { return; }

        if (recentView is not null)
        {
            if (Drafts.LoadedThreadViews.TryGetValue(recentView.Thread.Id, out var loaded))
            {
                _currentThreadView = loaded;
                _currentChatThread = loaded.Thread;
                await SelectConversationAsync(loaded.Thread.Id, cancellationToken, refreshMissing: false);
            }
            else
            {
                _currentThreadView = recentView;
                _currentChatThread = recentView.Thread;
                await ApplyThreadModelChoiceAsync(recentView, cancellationToken);
            }

            ApplyCurrentThreadAgentDefaults();
            return;
        }

        var created = await ChatApiClient.CreateThreadAsync(_selectedAgentId, _selectedProjectId, cancellationToken);
        if (selectionVersion != _conversationSelectionVersion) { return; }

        _currentChatThread = created;
        ApplyCurrentThreadAgentDefaults();
    }

    private async Task ApplyRequestedThreadAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_requestedThreadId))
        {
            return;
        }

        await SelectConversationAsync(_requestedThreadId, cancellationToken, refreshMissing: false);
    }

    private async Task StartNewChatAsync()
    {
        if (!await ConfirmWorkspaceAsync()) { return; }

        StartNewChatDraft();
        await InvokeAsync(StateHasChanged);
    }

    private void StartNewChatDraft()
    {
        _conversationSelectionVersion++;
        if (_currentThreadView is not null) { Drafts.LoadedThreadViews[_currentChatThread.Id] = _currentThreadView; }

        _isConversationLoading = false;
        _currentThreadView = null;
        _currentChatThread = CreateDraftThread();
    }

    private async Task LoadConversationAsync(string threadId)
    {
        if (threadId != _currentChatThread.Id && !await ConfirmWorkspaceAsync()) { return; }

        await SelectConversationAsync(threadId, default, refreshMissing: true);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Sidebar selections and chat links share recovery reporting and selection ordering.</summary>
    private async Task SelectConversationAsync(string threadId, CancellationToken cancellationToken, bool refreshMissing)
    {
        var selectionVersion = ++_conversationSelectionVersion;
        // Overlapping pending selections recover the last fully loaded chat or local draft.
        if (!_isConversationLoading) { _conversationBeforeLoading = (_currentChatThread, _currentThreadView); }

        var (previousThread, previousView) = _conversationBeforeLoading;
        if (previousView is not null && !_isConversationLoading) { Drafts.LoadedThreadViews[previousThread.Id] = previousView; }

        Drafts.LoadedThreadViews.TryGetValue(threadId, out var cached);
        var cachedMessages = cached?.Thread.DisplayMessages
            .Select(message => (message, message.Text, message.Segments)).ToArray();
        _currentThreadView = cached;
        var summary = _threads.FirstOrDefault(thread => thread.Id == threadId);
        _currentChatThread = cached?.Thread ?? summary ?? (previousThread.Id == threadId ? previousThread : new ChatThread { Id = threadId });
        if (summary is not null) { _currentChatThread.Title = summary.Title; }

        _selectedProjectId = _currentChatThread.ProjectId;
        _isConversationLoading = cached is null;
        await InvokeAsync(StateHasChanged);
        ChatThreadViewResponse? view;
        try
        {
            view = await ChatApiClient.GetThreadViewAsync(threadId, cancellationToken,
                firstMessageId: cached?.Thread.DisplayMessages.FirstOrDefault()?.MessageId);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            if (_disposeCts.IsCancellationRequested || selectionVersion != _conversationSelectionVersion) { return; }

            _isConversationLoading = false;
            _currentChatThread = previousThread;
            _currentThreadView = previousView;
            _selectedProjectId = previousThread.ProjectId;
            await InvokeAsync(StateHasChanged);
            Logger.LogWarning(ex, "Saved chat {ThreadId} could not be opened.", threadId);
            ToastService.Show(ex.Message, ToastType.Error);
            return;
        }
        // A slower response must not replace the user's newer selection.
        if (selectionVersion != _conversationSelectionVersion) { return; }

        _isConversationLoading = false;
        if (view is null)
        {
            if (refreshMissing)
            {
                await LoadThreadsAsync();
            }

            return;
        }

        if (cached is not null)
        {
            cached.ModelProviderId = view.ModelProviderId;
            // A cached view stays interactive while its refresh waits, so retain newer pages and projected replies.
            if (cached.Thread.DisplayMessages.Select(message => (message, message.Text, message.Segments))
                .SequenceEqual(cachedMessages!))
            {
                cached.Thread.DisplayMessages = view.Thread.DisplayMessages;
                cached.Thread.BranchableMessageIds = view.Thread.BranchableMessageIds;
                cached.Thread.Title = view.Thread.Title;
                cached.Thread.AgentId = view.Thread.AgentId;
                cached.Thread.AgentName = view.Thread.AgentName;
                cached.Thread.UpdatedAt = view.Thread.UpdatedAt;
                cached.Thread.SystemInstruction = view.Thread.SystemInstruction;
                cached.Thread.BranchOrigin = view.Thread.BranchOrigin;
                cached.Thread.SourceThreadAvailable = view.Thread.SourceThreadAvailable;
                cached.HasOlderMessages = view.HasOlderMessages;
                cached.HasSession = view.HasSession;
                cached.CanContinue = view.CanContinue;
                cached.ContinuationRecipientAgentId = view.ContinuationRecipientAgentId;
            }

            view = cached;
        }

        _currentThreadView = view;
        _currentChatThread = view.Thread;
        _selectedProjectId = view.Thread.ProjectId;
        Drafts.LoadedThreadViews[threadId] = view;
        await ApplyThreadModelChoiceAsync(view, cancellationToken);
    }

    /// <summary>Restores the model last chosen for an opened chat while chats without one keep the current pick.</summary>
    private async Task ApplyThreadModelChoiceAsync(ChatThreadViewResponse? view, CancellationToken cancellationToken)
    {
        if (view?.ModelProviderId is not { } providerId || providerId == _selectedRunModelProviderId ||
            !ChatModelProviders.Any(provider => provider.Id == providerId)) { return; }

        _selectedRunModelProviderId = providerId;
        await CheckServiceStatusCoreAsync(cancellationToken);
    }

    private async Task LoadConversationFromDrawerAsync(string threadId)
    {
        await CloseThreadsDrawerAsync();
        await LoadConversationAsync(threadId);
    }

    private Task CloseThreadsDrawerAsync() => SetThreadsDrawerAsync(false);

    private async Task SetThreadsDrawerAsync(bool open)
    {
        try
        {
            _utilsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/utils.js");
            await _utilsModule.InvokeVoidAsync("setCheckbox", "chat-threads-drawer-toggle", open);
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Failed to set chat threads drawer open state to {Open}.", open);
        }
    }

    private IJSObjectReference? _utilsModule;

    private async Task RenameConversationAsync((string ThreadId, string Title) rename)
    {
        try
        {
            await ChatApiClient.RenameThreadAsync(rename.ThreadId, rename.Title);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            ToastService.Show(
                Loc.GetOrDefault(ChatTextKeys.RenameThreadBusy, "Wait for the response to finish, then try renaming again."),
                ToastType.Warning);
            return;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            ToastService.Show(
                Loc.GetOrDefault(ChatTextKeys.RenameThreadFailed, "The conversation could not be renamed."),
                ToastType.Error);
            await RefreshThreadsAfterRenameAsync(rename.ThreadId);
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to rename chat thread {ThreadId}.", rename.ThreadId);
            ToastService.Show(
                Loc.GetOrDefault(ChatTextKeys.RenameThreadFailed, "The conversation could not be renamed."),
                ToastType.Error);
            return;
        }

        if (string.Equals(_currentChatThread.Id, rename.ThreadId, StringComparison.Ordinal))
        {
            _currentChatThread.Title = rename.Title;
        }

        await RefreshThreadsAfterRenameAsync(rename.ThreadId);
    }

    private async Task RefreshThreadsAfterRenameAsync(string threadId)
    {
        try
        {
            await LoadThreadsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to refresh the thread list after rename attempt for {ThreadId}.", threadId);
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task DeleteConversationAsync(string threadId)
    {
        // Save while the old thread's authorized file routes still exist, before DELETE.
        if (threadId == _currentChatThread.Id && !await ConfirmWorkspaceAsync()) { return; }

        _deletingThreadId = threadId;
        try
        {
            await ChatApiClient.DeleteThreadAsync(threadId);
            await LoadThreadsAsync();

            if (string.Equals(_currentChatThread.Id, threadId, StringComparison.Ordinal))
            {
                var selectionVersion = ++_conversationSelectionVersion;
                var recent = await UserScope.CurrentChatThreadService.GetRecentCurrentThreadAsync(
                    _currentChatThread.AgentId!, _selectedProjectId, _disposeCts.Token, messageLimit: 1);
                if (selectionVersion != _conversationSelectionVersion) { return; }

                if (recent is not null) { await LoadConversationAsync(recent.Id); }
                else { StartNewChatDraft(); }
            }

            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            _deletingThreadId = null;
        }
    }

    private async Task OnCurrentThreadChangedAsync(ChatThread thread)
    {
        if (thread.Id != _currentChatThread.Id && thread.DisplayMessages.Count > 0)
        {
            if (_currentThreadView is not null) { Drafts.LoadedThreadViews[_currentChatThread.Id] = _currentThreadView; }

            _currentChatThread = thread;
            _currentThreadView = null;
            await SelectConversationAsync(thread.Id, default, refreshMissing: true);
            await InvokeAsync(StateHasChanged);
            return;
        }

        _currentChatThread = thread;
        await InvokeAsync(StateHasChanged);
    }

    private Task OnToolActivityChangedAsync() => InvokeAsync(StateHasChanged);

    private async Task OnRunFinishedAsync()
    {
        _fileRefreshVersion++;
        if (_currentThreadView is null)
        {
            var thread = _currentChatThread;
            try
            {
                var view = await ChatApiClient.GetThreadViewAsync(thread.Id,
                    firstMessageId: thread.DisplayMessages.FirstOrDefault()?.MessageId);
                if (view is not null && ReferenceEquals(thread, _currentChatThread))
                {
                    _currentThreadView = new ChatThreadViewResponse
                    {
                        Thread = thread, HasOlderMessages = view.HasOlderMessages,
                        HasSession = view.HasSession, CanContinue = view.CanContinue,
                        ContinuationRecipientAgentId = view.ContinuationRecipientAgentId,
                        ModelProviderId = view.ModelProviderId
                    };
                    Drafts.LoadedThreadViews[thread.Id] = _currentThreadView;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                Logger.LogWarning(ex, "The completed chat view could not be refreshed for thread {ThreadId}.", thread.Id);
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task AddProjectAsync()
    {
        var result = await DialogService.ShowAsync<AddProjectDialog, ProjectDefinition>();
        if (!result.IsOk || result.Data is null) { return; }

        _projects = await ProjectsApiClient.GetProjectsAsync(_disposeCts.Token);
        await NewProjectChatAsync(result.Data.Id);
        _activityPanelView = ActivityPanelView.Standard;
    }

    private async Task NewProjectChatAsync(string? projectId)
    {
        if (!await ConfirmWorkspaceAsync()) { return; }

        _selectedProjectId = projectId;
        StartNewChatDraft();
        await InvokeAsync(StateHasChanged);
        await CloseThreadsDrawerAsync();
    }

    private async Task RemoveProjectAsync(string projectId)
    {
        var confirmed = await DialogService.ShowAsync<ConfirmDialog>(
            Loc[ChatTextKeys.ProjectRemove], Loc[ChatTextKeys.ProjectRemoveConfirm]);
        if (confirmed != DialogResult.Ok) { return; }

        if (projectId == _currentChatThread.ProjectId && !await ConfirmWorkspaceAsync()) { return; }

        try
        {
            await ProjectsApiClient.RemoveAsync(projectId, _disposeCts.Token);
            _projects = await ProjectsApiClient.GetProjectsAsync(_disposeCts.Token);
            _fileRefreshVersion++;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not remove project {ProjectId}.", projectId);
            ToastService.Show(ex.Message, ToastType.Error);
        }
    }

    private void OnBrowserChanged(object? sender, BrowserPageChange change)
    {
        if (!change.FocusRequested || change.Page.SessionId != _currentChatThread.Id || _disposeCts.IsCancellationRequested) { return; }

        _ = InvokeNotificationAsync(async () =>
        {
            if (_disposeCts.IsCancellationRequested || change.Page.SessionId != _currentChatThread.Id) { return; }

            var state = await UserScope.BrowserWorkspace!.GetStateAsync(change.Page.SessionId);
            if (state.SelectedPageId == change.Page.Id && !IsActivityOpen) { _activityPanelView = ActivityPanelView.Standard; }

            StateHasChanged();
        });
    }

    private Task OpenActivityAsync()
    {
        _activityPanelView = ActivityPanelView.Standard;
        return InvokeAsync(StateHasChanged);
    }

    private async Task OpenArtifactAsync(string artifactId)
    {
        if (_workspace is null) { return; }

        await _workspace.OpenArtifactAsync(artifactId);
        await OpenActivityAsync();
    }

    private async Task ToggleActivityWideAsync()
    {
        _activityPanelView = IsActivityWide ? ActivityPanelView.Standard : ActivityPanelView.Wide;
        await CloseThreadsDrawerAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task CollapseActivityAsync(bool restoreFocus = false)
    {
        // Full view hides the chat header, so return focus only after the collapsed layout renders.
        _restoreWorkspaceFocus = restoreFocus;
        _activityPanelView = ActivityPanelView.Collapsed;
        await CloseThreadsDrawerAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task SelectProviderAsync(Guid? providerId)
    {
        if (_selectedRunModelProviderId == providerId)
        {
            return;
        }

        _selectedRunModelProviderId = providerId;
        await CheckServiceStatusAsync();
    }

    private Task SelectAgentAsync(string? agentId)
    {
        _selectedAgentId = agentId;
        return Task.CompletedTask;
    }

    private Task CheckServiceStatusAsync()
    {
        // Keep a parameterless wrapper for the Razor event callback and delegate to the cancellable core method.
        return InvokeAsync(async () =>
        {
            await LoadChatConfigurationAsync(_disposeCts.Token);
            await CheckServiceStatusCoreAsync(_disposeCts.Token);
        });
    }

    private async Task CheckServiceStatusCoreAsync(CancellationToken cancellationToken = default)
    {
        var probeVersion = ++_serviceProbeVersion;
        _isCheckingService = true;
        await InvokeAsync(StateHasChanged);

        var selectedProvider = ResolveActiveModelProvider();

        if (selectedProvider is null)
        {
            _isServiceRunning = false;
            _serviceStatusMessage = string.Empty;
            _isCheckingService = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Intentionally do NOT pass the disposal token to the HTTP call: canceling an in-flight
        // HttpClient request causes the framework's System.Net.Http logger to emit a warning with
        // a full stack trace. The probe is cheap; let it finish and discard the result if the page
        // has been disposed.
        var result = await ManagementApiClient.TestModelProviderAsync(selectedProvider);

        // Opening chats in quick succession probes several providers, and only the latest one may report.
        if (_disposeCts.IsCancellationRequested || probeVersion != _serviceProbeVersion)
        {
            return;
        }

        _isServiceRunning = result.Success;
        _serviceStatusMessage = result.Message;
        _isCheckingService = false;
        await InvokeAsync(StateHasChanged);
    }

    private Task OnThreadChangedAsync(ChatThreadChangedEvent _)
    {
        return InvokeNotificationAsync(async () =>
        {
            await RefreshThreadsAfterChangeAsync();
            StateHasChanged();
        });
    }

    private async Task RefreshThreadsAfterChangeAsync()
    {
        var currentThreadId = _currentChatThread.Id;
        var shouldRecoverExpiredIncognito =
            _currentChatThread.IsIncognito &&
            !string.Equals(_deletingThreadId, currentThreadId, StringComparison.Ordinal);

        await LoadThreadsAsync();

        if (!shouldRecoverExpiredIncognito ||
            _threads.Any(thread => string.Equals(thread.Id, currentThreadId, StringComparison.Ordinal)))
        {
            return;
        }

        // The backend reaper removed the active ephemeral thread, so recover the composer locally.
        StartNewChatDraft();
        ToastService.Show(
            Loc.GetOrDefault(ChatTextKeys.IncognitoChatExpiredToast, "Incognito chat expired and was deleted."),
            ToastType.Info);
    }

    private void OnAppServiceStateChanged(object? sender, EventArgs args)
    {
        _ = InvokeNotificationAsync(async () =>
        {
            if (_disposeCts.IsCancellationRequested)
            {
                return;
            }

            var previousProvider = ResolveActiveModelProvider()?.Clone();
            await LoadChatConfigurationAsync(_disposeCts.Token);

            var currentProvider = ResolveActiveModelProvider();
            if (HasActiveModelProviderChanged(previousProvider, currentProvider))
            {
                await CheckServiceStatusCoreAsync(_disposeCts.Token);
                return;
            }

            StateHasChanged();
        });
    }

    private void OnProjectsChanged(object? sender, EventArgs args)
    {
        _ = InvokeNotificationAsync(() =>
        {
            if (_disposeCts.IsCancellationRequested) { return Task.CompletedTask; }

            _projects = UserScope.ProjectsSettings.Projects;
            _fileRefreshVersion++;
            StateHasChanged();
            return Task.CompletedTask;
        });
    }

    private static bool HasActiveModelProviderChanged(ModelProviderConfig? previousProvider, ModelProviderConfig? currentProvider)
    {
        if (previousProvider is null || currentProvider is null)
        {
            return previousProvider is not null || currentProvider is not null;
        }

        return !previousProvider.EqualsConfiguration(currentProvider);
    }

    private void ApplyCurrentThreadAgentDefaults()
    {
        _selectedProjectId = _currentChatThread.ProjectId;
        var agent = ResolveActiveAgent();
        _currentChatThread.AgentId ??= agent.Id;
        _currentChatThread.AgentName ??= agent.Name;
    }

    private void ApplyNavigationStateFromQuery()
    {
        _requestedThreadId = null;
        _redirectPath = null;
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        var query = QueryHelpers.ParseQuery(uri.Query);

        // OAuth returns also carry settings=connectedaccounts, so keep their result keys for the Settings page toast.
        if (query.ContainsKey("oauth_connected") || query.ContainsKey("oauth_error"))
        {
            _redirectPath = string.IsNullOrWhiteSpace(uri.Query)
                ? SettingsRegistry.GetSectionRoute("connectedaccounts")
                : $"{SettingsRegistry.GetSectionRoute("connectedaccounts")}{uri.Query}";
            return;
        }

        if (query.TryGetValue("settings", out var settingsSection) && !string.IsNullOrWhiteSpace(settingsSection))
        {
            var requestedSection = settingsSection.ToString();
            if (SettingsRegistry.GetSectionInfo(requestedSection) is not null)
            {
                _redirectPath = SettingsRegistry.GetSectionRoute(requestedSection);
                return;
            }
        }

        if (query.TryGetValue("threadId", out var threadId) && !string.IsNullOrWhiteSpace(threadId))
        {
            _requestedThreadId = threadId.ToString();
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        _ = InvokeAsync(async () =>
        {
            ApplyNavigationStateFromQuery();
            await ApplyRequestedThreadAsync(_disposeCts.Token);
            await EnsureCurrentThreadAsync(_disposeCts.Token);
            StateHasChanged();
        });
    }

    private static ChatThread CreateUninitializedThread() => new() { Id = string.Empty };

    private ChatThread CreateDraftThread()
    {
        var agent = ResolveNewThreadAgent();
        return new ChatThread
        {
            AgentId = agent.Id,
            ProjectId = _selectedProjectId,
            AgentName = agent.Name
        };
    }

    private AgentDefinition ResolveActiveAgent()
    {
        return AgentSelectionResolver.Resolve(
            MainAgent,
            CustomAgents,
            _selectedAgentId,
            _currentChatThread.AgentId,
            _currentChatThread.DisplayMessages.Count > 0);
    }

    private AgentDefinition ResolveNewThreadAgent()
    {
        return AgentSelectionResolver.Resolve(
            MainAgent,
            CustomAgents,
            _selectedAgentId,
            threadAgentId: null,
            conversationStarted: false);
    }

    private Guid? ResolveActiveModelProviderId()
    {
        if (_selectedRunModelProviderId is { } selectedProviderId &&
            ChatModelProviders.Any(provider => provider.Id == selectedProviderId))
        {
            return selectedProviderId;
        }

        return DefaultModelProviderId;
    }

    private ModelProviderConfig? ResolveActiveModelProvider()
    {
        var selectedProviderId = ResolveActiveModelProviderId();
        return selectedProviderId.HasValue
            ? ChatModelProviders.FirstOrDefault(provider => provider.Id == selectedProviderId.Value)
            : null;
    }

    private enum ActivityPanelView
    {
        Collapsed,
        Standard,
        Wide
    }

    private IEnumerable<ModelProviderConfig> ChatModelProviders =>
        ChatModelProviderSelection.GetChatProviders(
            _modelProviderConfiguration.ModelProviders, _modelProviderConfiguration.EmbeddingProviderIds);

    public void Dispose()
    {
        if (!string.IsNullOrWhiteSpace(_currentChatThread.Id))
        {
            Drafts.ActiveThread = _currentChatThread;
            if (_currentThreadView is not null) { Drafts.LoadedThreadViews[_currentChatThread.Id] = _currentThreadView; }
        }
        // Cancel before unhooking so any awaited startup work exits quickly and quietly.
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        NavigationManager.LocationChanged -= OnLocationChanged;
        if (SystemHubClient.OnThreadChangedAsync == OnThreadChangedAsync)
        {
            SystemHubClient.OnThreadChangedAsync = null;
        }

        UserScope.AppService.StateChanged -= OnAppServiceStateChanged;
        if (UserScope.BrowserWorkspace is { } browser) { browser.Changed -= OnBrowserChanged; }

        NodeBroker.StateChanged -= OnNodeStateChanged;

        UserScope.ProjectsSettings.Changed -= OnProjectsChanged;
        _ = _dashboardResizeModule?.DisposeAsync() ?? default;
        _ = _utilsModule?.DisposeAsync() ?? default;
    }
}
