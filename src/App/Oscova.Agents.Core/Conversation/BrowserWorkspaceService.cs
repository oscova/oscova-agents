using System.Text.Json;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Browsing;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Harness;
using Oscova.Agents.Secrets;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Binds one user's browser contribution to owned chats and encrypted tab restoration.</summary>
public sealed class BrowserWorkspaceService(string userId, IChatThreadRepository threads, IUserDirectoryService directories,
    IContentProtector protector, RunContextAccessor runs, ILogger<BrowserWorkspaceService> logger) : IDisposable
{
    private const string Purpose = "browser-tabs";
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private Dictionary<string, BrowserWorkspaceState>? _states;
    private readonly HashSet<string> _incognito = new(StringComparer.Ordinal);
    private IBrowserCapability? _capability;
    private bool _conflict;
    private bool _disposed;
    private string StatePath => Path.Combine(directories.GetUserDirectoryPath(userId), "browser-tabs.json.enc");

    /// <summary>Reports browser changes to clients displaying the affected chat.</summary>
    public event EventHandler<BrowserPageChange>? Changed;

    /// <summary>Gets whether exactly one backend was contributed.</summary>
    public bool IsAvailable => _capability is not null && !_conflict;

    /// <summary>Gets the existing per-user plugin runtime after capability resolution.</summary>
    public IBrowserCapability Capability => IsAvailable ? _capability! : throw new ToolFailureException(
        _conflict ? "Multiple browser plugins are enabled." : "No browser plugin is available.",
        "Enable exactly one browser plugin, then restart Oscova.");

    /// <summary>Binds the already materialized contribution without creating another runtime.</summary>
    public void Bind(IReadOnlyList<IBrowserCapability> contributions)
    {
        _conflict = contributions.Count > 1;
        _capability = contributions.Count == 1 ? contributions[0] : null;
        if (_capability is not null) { _capability.PageChanged += OnPageChanged; }
    }

    /// <summary>Resolves the requesting user and root chat from the current trusted invocation.</summary>
    public async Task<BrowserSessionScope> ResolveToolSessionAsync(CancellationToken cancellationToken)
    {
        var run = runs.Current;
        if (run?.AirGapped == true)
        {
            throw new ToolFailureException("Browser tools are unavailable because this run is restricted by Offline mode or a Local-only agent.",
                "Ask the user to review Offline and Local-only settings, then start an unrestricted chat turn before retrying.");
        }

        if (run is null || run.UserId != userId || run.ExecutionKind != RunExecutionKind.WebChat)
        { throw new ToolFailureException("Browser tools require an active web chat belonging to the requesting user."); }

        return await ResolveSessionAsync(run.ExecutionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Authorizes a manual or tool request against the unlocked owning chat.</summary>
    public async Task<BrowserSessionScope> ResolveSessionAsync(string chatId, CancellationToken cancellationToken = default)
    {
        EnsureUnlocked();
        _ = Capability;
        var thread = await threads.GetThreadAsync(userId, chatId, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The browser chat is unavailable.");
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (thread.IsIncognito) { _incognito.Add(chatId); }

            return new(thread.Id, thread.IsIncognito, _states!.GetValueOrDefault(chatId)?.SelectedPageId);
        }
        finally { _stateGate.Release(); }
    }

    /// <summary>Returns dormant and live tabs without starting Chromium.</summary>
    public async Task<BrowserWorkspaceState> GetStateAsync(string chatId, CancellationToken cancellationToken = default)
    {
        // A new chat's context estimate runs before its first message saves it, and it has no tabs yet.
        if (!await threads.ThreadExistsAsync(userId, chatId, cancellationToken).ConfigureAwait(false)) { return new([], null); }

        await ResolveSessionAsync(chatId, cancellationToken).ConfigureAwait(false);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _states!.GetValueOrDefault(chatId) ?? new([], null); }
        finally { _stateGate.Release(); }
    }

    /// <summary>Runs a manual command against the same page used by agent tools.</summary>
    public async Task<BrowserResult> ExecuteAsync(string chatId, BrowserCommand command, CancellationToken cancellationToken = default)
    {
        var session = await ResolveSessionAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (command.Kind == "list")
        { return new(Pages: (await GetStateAsync(chatId, cancellationToken).ConfigureAwait(false)).Pages); }

        if (command.PageId is { } pageId && !Capability.GetPages(chatId).Any(page => page.Id == pageId))
        {
            var state = await GetStateAsync(chatId, cancellationToken).ConfigureAwait(false);
            // A missing tab is recoverable input, after the owning chat's authorization has passed.
            var saved = state.Pages.FirstOrDefault(page => page.Id == pageId)
                ?? throw new ToolFailureException("pageId does not identify an available tab in this chat.",
                    "List this chat's browser tabs and use one of their IDs.");
            if (command.Kind == "close") { await RemovePageAsync(chatId, pageId, cancellationToken).ConfigureAwait(false); return new(); }

            await Capability.ExecuteAsync(session, new("open", saved.Id, saved.Url == "about:blank" ? null : saved.Url), cancellationToken).ConfigureAwait(false);
        }

        var result = await Capability.ExecuteAsync(session, command, cancellationToken).ConfigureAwait(false);
        // Closed-page results preserve the surviving tab's selection.
        if (result.Pages is null && command.Kind is "open" or "focus")
        { await SelectAsync(chatId, result.Page?.Id ?? command.PageId, cancellationToken).ConfigureAwait(false); }

        return result;
    }

    /// <summary>Applies invocation policy before sharing the manual command and restoration path.</summary>
    public async Task<BrowserResult> ExecuteToolAsync(BrowserCommand command, CancellationToken cancellationToken)
    {
        var session = await ResolveToolSessionAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(session.Id, command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the selected browser tab while leaving unrelated document selection to the viewer.</summary>
    public async Task SelectAsync(string chatId, string? pageId, CancellationToken cancellationToken = default)
    {
        await ResolveSessionAsync(chatId, cancellationToken).ConfigureAwait(false);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _states!.GetValueOrDefault(chatId) ?? new BrowserWorkspaceState([], null);
            if (pageId is not null && !state.Pages.Any(page => page.Id == pageId) && !Capability.GetPages(chatId).Any(page => page.Id == pageId))
            { throw new UnauthorizedAccessException("The browser tab is unavailable in this chat."); }

            _states![chatId] = state with { SelectedPageId = pageId };
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
    }

    private async void OnPageChanged(object? sender, BrowserPageChange change)
    {
        if (_disposed) { return; }

        if (change.Error is not null) { Changed?.Invoke(this, change); return; }

        try
        {
            await _stateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || !protector.IsAvailable(userId) || !await threads.ThreadExistsAsync(userId, change.Page.SessionId).ConfigureAwait(false)) { return; }

                await LoadAsync(CancellationToken.None).ConfigureAwait(false);
                var state = _states!.GetValueOrDefault(change.Page.SessionId) ?? new BrowserWorkspaceState([], null);
                var pages = state.Pages.ToList();
                var index = pages.FindIndex(page => page.Id == change.Page.Id);
                if (change.Closed) { if (index >= 0) { pages.RemoveAt(index); } }
                else
                {
                    var live = Capability.GetPages(change.Page.SessionId).FirstOrDefault(page => page.Id == change.Page.Id);
                    if (live is null) { return; }

                    if (index >= 0) { pages[index] = live; }
                    else
                    {
                        var opener = pages.FindIndex(page => page.Id == live.OpenerId);
                        pages.Insert(opener >= 0 ? opener + 1 : pages.Count, live);
                    }
                }

                var selected = state.SelectedPageId;
                if (change.Closed && selected == change.Page.Id)
                { selected = pages.FirstOrDefault(page => page.Id == change.Page.OpenerId)?.Id ?? pages.LastOrDefault()?.Id; }
                else if (change.FocusRequested && (change.Page.OpenerId is null || selected == change.Page.OpenerId)) { selected = change.Page.Id; }

                _states![change.Page.SessionId] = new(pages, selected);
                await SaveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { _stateGate.Release(); }

            Changed?.Invoke(this, change);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Browser tab persistence failed for user {UserId}.", userId); }
    }

    private async Task RemovePageAsync(string chatId, string pageId, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _states!.GetValueOrDefault(chatId);
            if (state is null) { return; }

            var pages = state.Pages.Where(page => page.Id != pageId).ToArray();
            _states![chatId] = new(pages, state.SelectedPageId == pageId ? pages.LastOrDefault()?.Id : state.SelectedPageId);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
    }

    /// <summary>Removes browser state through every existing chat-deletion entry point.</summary>
    public async Task RemoveChatsAsync(IReadOnlyList<string> chatIds, CancellationToken cancellationToken = default)
    {
        foreach (var chatId in chatIds)
        { if (_capability is not null) { await _capability.CloseSessionAsync(chatId, cancellationToken).ConfigureAwait(false); } }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var chatId in chatIds) { _states!.Remove(chatId); _incognito.Remove(chatId); }

            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
    }

    private void EnsureUnlocked()
    { if (!protector.IsAvailable(userId)) { throw new UnauthorizedAccessException("Unlock the vault before accessing browser tabs."); } }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        EnsureUnlocked();
        if (_states is not null) { return; }

        if (!File.Exists(StatePath)) { _states = new(StringComparer.Ordinal); return; }

        var ciphertext = await File.ReadAllTextAsync(StatePath, cancellationToken).ConfigureAwait(false);
        var plaintext = protector.Unprotect(userId, Purpose, ciphertext) ?? throw new IOException("Browser tab state could not be decrypted.");
        var saved = JsonSerializer.Deserialize<Dictionary<string, SavedChat>>(plaintext) ?? [];
        _states = saved.ToDictionary(pair => pair.Key, pair => new BrowserWorkspaceState(
            pair.Value.Pages.Select(page => new BrowserPage(page.Id, pair.Key, page.Url, page.Title)).ToArray(), pair.Value.SelectedPageId), StringComparer.Ordinal);
    }

    private Task SaveAsync(CancellationToken cancellationToken)
    {
        EnsureUnlocked();
        var persisted = _states!.Where(pair => !_incognito.Contains(pair.Key)).ToDictionary(pair => pair.Key,
            pair => new SavedChat(pair.Value.Pages.Select(page => new SavedPage(page.Id, page.Url, page.Title)).ToArray(), pair.Value.SelectedPageId));
        return FileUtil.WriteAllTextAtomicAsync(StatePath, protector.Protect(userId, Purpose, JsonSerializer.Serialize(persisted)), cancellationToken);
    }

    /// <summary>Detaches persistence before plugin disposal closes live tabs at application shutdown.</summary>
    public void Dispose()
    { _disposed = true; if (_capability is not null) { _capability.PageChanged -= OnPageChanged; } }

    private sealed record SavedPage(string Id, string Url, string Title);
    private sealed record SavedChat(IReadOnlyList<SavedPage> Pages, string? SelectedPageId);
}

/// <summary>Stores only chat-owned browser tabs and selection needed for restoration.</summary>
public sealed record BrowserWorkspaceState(IReadOnlyList<BrowserPage> Pages, string? SelectedPageId);
