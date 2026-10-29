using Oscova.Agents.Browsing;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Components.Chat;

public partial class DetailsSidePanel
{
    private IReadOnlyList<BrowserPage> _browserPages = [];
    private bool _openingBrowser;
    private bool _disposed;

    protected override void OnInitialized()
    { if (UserScope.BrowserWorkspace is { } browser) { browser.Changed += OnBrowserChanged; } }

    private string BrowserKey(string pageId) => $"browser:{Thread.Id}:{pageId}";
    private string BrowserLoadingKey => $"browser-loading:{Thread.Id}";

    // Browser pages use the existing document strip, with selection and lifetime owned by their chat.
    private async Task RefreshBrowserDocumentsAsync(bool restoreSelection)
    {
        var threadId = Thread.Id;
        var editing = _editing;
        // The dashboard renders an empty identity while it loads the current chat.
        if (string.IsNullOrEmpty(threadId) || UserScope.BrowserWorkspace is not { IsAvailable: true } browser ||
            !await UserScope.ChatThreadRepository.ThreadExistsAsync(UserScope.UserId, threadId)) { return; }

        try { await BrowserAccess.RequireAsync(); }
        catch (UnauthorizedAccessException) { return; }

        var state = await browser.GetStateAsync(threadId);
        // A delayed refresh cannot reconcile tabs into another conversation's editing state.
        if (_disposed || Thread.Id != threadId || !ReferenceEquals(editing, _editing)) { return; }

        _browserPages = state.Pages;
        var loading = _editing.Documents.FirstOrDefault(document => document.Key == BrowserLoadingKey);
        if (loading is not null && restoreSelection && state.SelectedPageId is not null)
        {
            _editing.Documents.Remove(loading);
            _documentButtons.Remove(loading.Key);
            loading = null;
        }

        foreach (var old in _editing.Documents.Where(document => document.Key.StartsWith("browser:", StringComparison.Ordinal)).ToArray())
        {
            if (_browserPages.Any(page => BrowserKey(page.Id) == old.Key)) { continue; }

            _editing.Documents.Remove(old);
            _documentButtons.Remove(old.Key);
        }

        foreach (var page in _browserPages)
        {
            var key = BrowserKey(page.Id);
            var index = _editing.Documents.ToList().FindIndex(tab => tab.Key == key);
            // Partial startup metadata must not add a second tab beside the pending document.
            if (index < 0 && loading is not null) { continue; }

            var document = new WorkspaceDocumentTab(key, string.IsNullOrWhiteSpace(page.Title) ? Loc[ChatTextKeys.BrowserNewTab] : page.Title,
                page.Url, "lucide:globe", () => SelectBrowserAsync(page.Id), () => CloseBrowserAsync(page.Id));
            if (index >= 0) { _editing.Documents[index] = document; }
            else
            {
                var opener = _editing.Documents.ToList().FindIndex(tab => page.OpenerId is not null && tab.Key == BrowserKey(page.OpenerId));
                _editing.Documents.Insert(opener >= 0 ? opener + 1 : _editing.Documents.Count, document);
            }
        }
        // A background popup can request focus without becoming the selected browser tab.
        if ((restoreSelection && state.SelectedPageId is not null) ||
            (_selectedTab == DetailsTab.Browser && _editing.SelectedDocumentKey != BrowserLoadingKey))
        {
            _editing.SelectedDocumentKey = state.SelectedPageId is { } selected ? BrowserKey(selected) : null;
            if (state.SelectedPageId is not null) { _selectedTab = DetailsTab.Browser; }
            else if (_selectedTab == DetailsTab.Browser) { _selectedTab = DetailsTab.Files; }
        }
    }

    private void OnBrowserChanged(object? sender, BrowserPageChange change)
    {
        if (_disposed || change.Page.SessionId != Thread.Id) { return; }

        _ = InvokeAsync(async () =>
        {
            if (_disposed || change.Page.SessionId != Thread.Id) { return; }

            if (change.Error is not null) { ToastService.Show(change.Error, ToastType.Warning); return; }

            if (change.FocusRequested) { await _editing.CaptureAsync(); }

            await RefreshBrowserDocumentsAsync(change.FocusRequested);
            StateHasChanged();
        });
    }

    private async Task OpenBrowserAsync()
    {
        var thread = Thread;
        var editing = _editing;
        var previousKey = editing.SelectedDocumentKey;
        var previousTab = _selectedTab;
        var loadingKey = BrowserLoadingKey;
        _openingBrowser = true;
        // Native browser provisioning must not delay the workspace's visible progress.
        editing.Documents.Add(new(loadingKey, Loc[ChatTextKeys.BrowserNewTab], string.Empty, "lucide:globe",
            async () => { await editing.CaptureAsync(); editing.SelectedDocumentKey = loadingKey; _selectedTab = DetailsTab.Browser; },
            () => Task.CompletedTask));
        editing.SelectedDocumentKey = loadingKey;
        _selectedTab = DetailsTab.Browser;
        try
        {
            await editing.CaptureAsync();
            await BrowserAccess.RequireAsync();
            if (!await UserScope.ChatThreadRepository.ThreadExistsAsync(UserScope.UserId, thread.Id))
            { await ChatApi.CreateThreadAsync(thread); }

            await UserScope.BrowserWorkspace!.ExecuteAsync(thread.Id, new("open"));
            if (!_disposed && Thread.Id == thread.Id) { await RefreshBrowserDocumentsAsync(true); }
        }
        catch (Exception exception) { ToastService.Show(exception.Message, ToastType.Error); }
        finally
        {
            var loading = editing.Documents.FirstOrDefault(document => document.Key == loadingKey);
            if (loading is not null) { editing.Documents.Remove(loading); }

            _documentButtons.Remove(loadingKey);
            if (!_disposed && Thread.Id == thread.Id && editing.SelectedDocumentKey == loadingKey)
            {
                editing.SelectedDocumentKey = previousKey;
                _selectedTab = previousTab;
            }

            _openingBrowser = false;
        }
    }

    private async Task SelectBrowserAsync(string pageId)
    {
        if (_editing.IsBusy) { return; }

        try { await BrowserAccess.RequireAsync(); }
        catch (UnauthorizedAccessException exception) { ToastService.Show(exception.Message, ToastType.Error); return; }

        await _editing.CaptureAsync();
        await UserScope.BrowserWorkspace!.SelectAsync(Thread.Id, pageId);
        _editing.SelectedDocumentKey = BrowserKey(pageId);
        _selectedTab = DetailsTab.Browser;
    }

    private async Task CloseBrowserAsync(string pageId)
    {
        try
        {
            await BrowserAccess.RequireAsync();
            await UserScope.BrowserWorkspace!.ExecuteAsync(Thread.Id, new("close", pageId));
            await RefreshBrowserDocumentsAsync(false);
        }
        catch (Exception exception) { ToastService.Show(exception.Message, ToastType.Error); }
    }

    private async Task ClearBrowserSelectionAsync()
    {
        if (_selectedTab == DetailsTab.Browser && UserScope.BrowserWorkspace is { IsAvailable: true } browser)
        {
            try { await BrowserAccess.RequireAsync(); }
            catch (UnauthorizedAccessException) { return; }

            await browser.SelectAsync(Thread.Id, null);
        }
    }

    /// <summary>Detaches chat-specific browser updates when the workspace leaves the circuit.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (UserScope.BrowserWorkspace is { } browser) { browser.Changed -= OnBrowserChanged; }
    }
}
