using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Oscova.Agents.Browsing;
using Oscova.Agents.Harness;

namespace Oscova.Agents.Plugins.Browser;

/// <summary>Owns one user's browser pages shared by agent tools and authorized viewers.</summary>
[DisplayName("Browser")]
public sealed partial class BrowserToolset(AgentPluginContext context) : IAgentToolset, IBrowserCapability
{
    private const int PageActionTimeoutMilliseconds = 10000;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly ConcurrentDictionary<string, PageState> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IBrowserContext> _privateContexts = new(StringComparer.Ordinal);
    private readonly ILogger _logger = context.LoggerFactory.CreateLogger<BrowserToolset>();
    private IPlaywright? _playwright;
    private IBrowserContext? _regular;
    private IBrowser? _privateBrowser;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<BrowserPageChange>? PageChanged;

    /// <inheritdoc />
    public IReadOnlyList<BrowserPage> GetPages(string sessionId) =>
        _pages.Values.Where(page => page.Info.SessionId == sessionId).Select(page => page.Info).ToArray();

    /// <inheritdoc />
    public async Task<BrowserResult> ExecuteAsync(BrowserSessionScope session, BrowserCommand command, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lifecycleHeld = true;
        SemaphoreSlim? pageCommands = null;
        PageState? active = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (command.Kind == "list") { return new(Pages: GetPages(session.Id)); }
            if (command.Kind == "open")
            {
                // Another caller may have restored this saved tab while this command waited for the lock.
                if (command.PageId is { } restoredId && _pages.ContainsKey(restoredId))
                { return new(Page: FindPage(session.Id, restoredId).Info); }
                if (_pages.Count >= 8) { throw new ToolFailureException("Eight browser tabs are already live.", "Close a browser tab before opening another."); }
                var startingRegularContext = !session.IsEphemeral && _regular is null;
                var browserContext = await GetContextAsync(session).ConfigureAwait(false);
                // Only reuse Chromium's initial page. Unregistered pages in an existing context may be pending popups.
                var page = (startingRegularContext ? browserContext.Pages.FirstOrDefault() : null)
                    ?? await browserContext.NewPageAsync().ConfigureAwait(false);
                var state = await AddPageAsync(session, page, command.PageId, null).ConfigureAwait(false);
                active = state;
                await state.Commands.WaitAsync(cancellationToken).ConfigureAwait(false);
                pageCommands = state.Commands;
                _commands.Release();
                lifecycleHeld = false;
                if (!string.IsNullOrWhiteSpace(command.Text)) { await NavigateAsync(page, command.Text).ConfigureAwait(false); }
                // Restoring a saved page for a background action must preserve the viewer's selection.
                await RefreshAsync(state, focus: command.PageId is null).ConfigureAwait(false);
                return new(Page: state.Info);
            }

            var current = FindPage(session.Id, command.PageId);
            active = current;
            var target = current.Page;
            if (command.Kind is not ("close" or "focus"))
            {
                // Order website actions per page, leaving close and sibling viewers free during a load.
                _commands.Release();
                lifecycleHeld = false;
                await current.Commands.WaitAsync(cancellationToken).ConfigureAwait(false);
                pageCommands = current.Commands;
                ObjectDisposedException.ThrowIf(_disposed, this);
                FindPage(session.Id, command.PageId);
            }
            switch (command.Kind)
            {
                case "close":
                    await ClosePageAsync(current).ConfigureAwait(false);
                    await StopIfEmptyAsync().ConfigureAwait(false);
                    return new(Pages: GetPages(session.Id));
                case "focus":
                    await RefreshAsync(current, focus: true).ConfigureAwait(false);
                    return new(Page: current.Info);
                case "navigate": await NavigateAsync(target, command.Text).ConfigureAwait(false); break;
                case "read": return await ObserveAsync(current, command.Reference, command.X == 0 ? 20 : checked((int)command.X)).ConfigureAwait(false);
                case "click":
                    await ShowTargetAsync(current, command.Reference).ConfigureAwait(false);
                    await ResolveReference(current, command.Reference).ClickAsync().ConfigureAwait(false);
                    break;
                case "fill":
                    if (command.Text is null || command.Text.Length > 16384) { throw new ArgumentException("text must contain at most 16384 characters."); }
                    await ShowTargetAsync(current, command.Reference).ConfigureAwait(false);
                    await ResolveReference(current, command.Reference).ClickAsync().ConfigureAwait(false);
                    await ResolveReference(current, command.Reference).FillAsync(command.Text).ConfigureAwait(false);
                    break;
                case "select":
                    if (command.Values is not { Count: > 0 and <= 32 }) { throw new ArgumentException("values must contain 1 to 32 options."); }
                    await ShowTargetAsync(current, command.Reference).ConfigureAwait(false);
                    await ResolveReference(current, command.Reference).SelectOptionAsync(command.Values).ConfigureAwait(false);
                    break;
                case "back": await target.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false); break;
                case "forward": await target.GoForwardAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false); break;
                case "reload": await target.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false); break;
                case "wait":
                    if (string.IsNullOrWhiteSpace(command.Text) || command.Text.Length > 1024 || command.X is < 1 or > 30)
                    { throw new ArgumentException("text must contain 1 to 1024 characters and timeout must be 1 to 30 seconds."); }
                    await target.GetByText(command.Text).First.WaitForAsync(new() { Timeout = command.X * 1000 }).ConfigureAwait(false);
                    break;
                case "cancel-upload": current.Chooser = null; break;
                case "upload":
                    if (command.Files is not { Count: > 0 and <= 8 } || command.Files.Sum(file => (long)file.Data.Length) > 32 * 1024 * 1024)
                    { throw new ArgumentException("files must contain 1 to 8 files totalling at most 32 MiB."); }
                    var files = command.Files.Select(file => new FilePayload { Name = Path.GetFileName(file.Name), MimeType = file.ContentType, Buffer = file.Data.ToArray() }).ToArray();
                    if (command.Reference is not null) { await ResolveReference(current, command.Reference).SetInputFilesAsync(files).ConfigureAwait(false); }
                    else if (current.Chooser is { } chooser) { await chooser.SetFilesAsync(files).ConfigureAwait(false); current.Chooser = null; }
                    else { throw new ArgumentException("reference is required when no website file chooser is open."); }
                    break;
                case "downloads": return new(Page: current.Info, Downloads: current.Downloads.Values.Select(download => download.Info).ToArray());
                default:
                    if (command.Kind == "scroll" && command.Reference is not null)
                    { await ResolveReference(current, command.Reference).HoverAsync().ConfigureAwait(false); }
                    var input = await SendInputAsync(current, command).ConfigureAwait(false);
                    return input ?? new(Page: current.Info);
            }
            // A successful website action may close its own popup instead of leaving a page to observe.
            if (target.IsClosed) { return new(Pages: GetPages(session.Id), Text: "The page closed."); }
            try { await RefreshAsync(current).ConfigureAwait(false); }
            catch (PlaywrightException) when (target.IsClosed) { return new(Pages: GetPages(session.Id), Text: "The page closed."); }
            return new(Page: current.Info);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            // Navigation can abort before Chromium publishes the requested page closure.
            if (active?.CloseCompleted is { } closed) { await closed.ConfigureAwait(false); }
            // Closing a page intentionally interrupts any website action still waiting on it.
            if (active?.Page.IsClosed == true) { return new(Pages: GetPages(session.Id), Text: "The page closed."); }
            // A link can commit navigation while pointer-up probes the old document for a native dropdown.
            if (command.Kind == "up" && active is not null && (active.Page.IsClosed ||
                exception.Message.Contains("Execution context was destroyed", StringComparison.Ordinal)))
            { return new(Page: active.Info); }
            if (active is not null && command.Kind is "open" or "navigate" or "back" or "forward" or "reload")
            {
                var failure = Regex.Match(exception.Message, "net::ERR_[A-Z_]+", RegexOptions.None, TimeSpan.FromSeconds(1));
                // Failure precedes the error-page commit. Its normal navigation event will refresh history.
                if (failure.Success) { return new(Page: active.Info, Text: $"Navigation failed ({failure.Value})."); }
            }
            // Vendor call logs may contain field values and page content. Log only the failure type and operation identity.
            _logger.LogError("Browser action {Action} failed for session {SessionId} page {PageId}: {FailureType}.", command.Kind, session.Id, command.PageId, exception.GetType().Name);
            if (command.Kind == "open" && active is null)
            { throw new ToolFailureException("Chromium could not start on the server. Ask the server operator to check disk space and browser profile access. On Linux, install the system libraries listed at https://playwright.dev/dotnet/docs/browsers#install-system-dependencies, then open a browser tab again."); }
            throw new ToolFailureException("The browser action could not complete.", "Read the page again for current references. If sign-in or verification is required, ask the user for help and end this turn.");
        }
        finally
        {
            pageCommands?.Release();
            if (lifecycleHeld) { _commands.Release(); }
        }
    }

    private async Task<IBrowserContext> GetContextAsync(BrowserSessionScope session)
    {
        if (session.IsEphemeral && _privateContexts.TryGetValue(session.Id, out var existing)) { return existing; }
        if (!session.IsEphemeral && _regular is not null) { return _regular; }
        // Managed plugins load from streams, so the vendor driver needs its restored package directory explicitly.
        var packageRoot = Path.Combine(context.PluginDirectory, "packages", "microsoft.playwright",
            typeof(Playwright).Assembly.GetName().Version!.ToString(3));
        Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", packageRoot);
        // NuGet copying does not preserve Unix executable permission.
        if (!OperatingSystem.IsWindows())
        {
            var node = Path.Combine(packageRoot, ".playwright", "node", OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "darwin-x64" : "darwin-arm64") : "linux-x64", "node");
            if (File.Exists(node)) { File.SetUnixFileMode(node, File.GetUnixFileMode(node) | UnixFileMode.UserExecute); }
        }
        _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(_playwright.Chromium.ExecutablePath))
        {
            // The vendor installer owns revision matching and interrupted-download recovery, and cannot be canceled through this API.
            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "--no-shell", "chromium"])).ConfigureAwait(false);
            if (exitCode != 0) { throw new ToolFailureException("Chromium installation failed. Ask the server operator to check internet access and disk space, then open a browser tab again. On Linux, install the system libraries listed at https://playwright.dev/dotnet/docs/browsers#install-system-dependencies."); }
        }
        if (session.IsEphemeral)
        {
            _privateBrowser ??= await _playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "chromium" }).ConfigureAwait(false);
            var isolated = await _privateBrowser.NewContextAsync(new() { ViewportSize = new() { Width = 1000, Height = 650 }, AcceptDownloads = true }).ConfigureAwait(false);
            isolated.SetDefaultTimeout(PageActionTimeoutMilliseconds);
            isolated.SetDefaultNavigationTimeout(30000);
            _privateContexts.Add(session.Id, isolated);
            return isolated;
        }
        if (context.UserDataDirectory is null) { throw new InvalidOperationException("A user-owned data directory is required for browsing."); }
        _regular = await _playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(context.UserDataDirectory, "browser-profile"),
            new() { Headless = true, Channel = "chromium", ViewportSize = new() { Width = 1000, Height = 650 }, AcceptDownloads = true }).ConfigureAwait(false);
        _regular.SetDefaultTimeout(PageActionTimeoutMilliseconds);
        _regular.SetDefaultNavigationTimeout(30000);
        return _regular;
    }

    private async Task<PageState> AddPageAsync(BrowserSessionScope session, IPage page, string? id, string? opener)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
        var state = new PageState(page, cdp, new(id ?? Guid.NewGuid().ToString("N"), session.Id, page.Url, "New tab", opener));
        if (!_pages.TryAdd(state.Info.Id, state)) { await page.CloseAsync().ConfigureAwait(false); throw new ArgumentException("pageId already exists."); }
        page.Close += (_, _) =>
        {
            _pages.TryRemove(state.Info.Id, out _);
            foreach (var viewer in state.Viewers.Values) { viewer.Writer.TryComplete(); }
            PageChanged?.Invoke(this, new(state.Info, closed: true));
            _ = StopAfterPageClosedAsync();
        };
        page.DOMContentLoaded += (_, _) => _ = RefreshObservedAsync(state);
        page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) { state.Pointer = null; state.References.Clear(); _ = RefreshObservedAsync(state); } };
        page.Popup += (_, popup) => _ = AddPopupAsync(session, popup, state.Info.Id);
        page.FileChooser += (_, chooser) => { state.Chooser = chooser; _ = RefreshObservedAsync(state); };
        page.Download += (_, download) => _ = TrackDownloadAsync(state, download);
        cdp.Event("Page.screencastFrame").OnEvent += (_, payload) => _ = ReceiveFrameAsync(state, cdp, payload);
        await cdp.SendAsync("Page.startScreencast", new() { ["format"] = "jpeg", ["quality"] = 80, ["maxWidth"] = 1920, ["maxHeight"] = 1080 }).ConfigureAwait(false);
        PageChanged?.Invoke(this, new(state.Info));
        return state;
    }

    private async Task AddPopupAsync(BrowserSessionScope session, IPage page, string opener)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !_pages.ContainsKey(opener) || _pages.Count >= 8)
            {
                await page.CloseAsync().ConfigureAwait(false);
                if (!_disposed && _pages.TryGetValue(opener, out var owner))
                { PageChanged?.Invoke(this, new(owner.Info, error: "Eight browser tabs are already live. Close a browser tab before opening another.")); }
                return;
            }
            var state = await AddPageAsync(session, page, null, opener).ConfigureAwait(false);
            await RefreshAsync(state, focus: true).ConfigureAwait(false);
        }
        catch (PlaywrightException exception) { _logger.LogWarning("Could not attach browser popup for session {SessionId}: {FailureType}.", session.Id, exception.GetType().Name); }
        finally { _commands.Release(); }
    }

    private async Task RefreshObservedAsync(PageState state)
    {
        try { if (!state.Page.IsClosed) { await RefreshAsync(state).ConfigureAwait(false); } }
        catch (PlaywrightException exception) { if (!state.Page.IsClosed) { _logger.LogWarning("Could not refresh browser page {PageId}: {FailureType}.", state.Info.Id, exception.GetType().Name); } }
    }

    private async Task RefreshAsync(PageState state, bool focus = false)
    {
        // Chromium owns history, including same-document navigation and blank entries.
        var history = (await state.Cdp.SendAsync("Page.getNavigationHistory").ConfigureAwait(false))!.Value;
        var index = history.GetProperty("currentIndex").GetInt32();
        var entry = history.GetProperty("entries")[index];
        state.Info = state.Info with
        {
            Url = entry.GetProperty("url").GetString()!, Title = entry.GetProperty("title").GetString()!, HasFileChooser = state.Chooser is not null,
            CanGoBack = index > 0, CanGoForward = index + 1 < history.GetProperty("entries").GetArrayLength()
        };
        PageChanged?.Invoke(this, new(state.Info, focusRequested: focus));
    }

    private static async Task NavigateAsync(IPage page, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || url!.Length > 4096)
        { throw new ArgumentException("url must be an absolute HTTP or HTTPS URL of at most 4096 characters."); }
        await page.GotoAsync(parsed.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
    }

    private static ILocator ResolveReference(PageState state, string? reference)
    {
        if (reference is null || !state.References.ContainsKey(reference))
        { throw new ToolFailureException("reference is not in the last observation for this tab.", "Read this tab again, then use a reference from that result."); }
        return state.Page.Locator("aria-ref=" + reference);
    }

    private static async Task<BrowserResult> ObserveAsync(PageState state, string? reference = null, int depth = 20)
    {
        if (depth is < 1 or > 40) { throw new ArgumentException("depth must be between 1 and 40."); }
        if (state.Page.Url == "chrome-error://chromewebdata/")
        { throw new ToolFailureException("The website could not load.", "Check the tab's address and the browser host's network or DNS connection before retrying."); }
        var locator = reference is null ? state.Page.Locator("body") : ResolveReference(state, reference);
        var snapshot = await locator.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai, Depth = depth, Timeout = 10000 }).ConfigureAwait(false);
        const int limit = 24000;
        if (snapshot.Length > limit)
        {
            var boundary = snapshot.LastIndexOf('\n', limit);
            snapshot = snapshot[..Math.Max(boundary, 0)] + "\n[Observation truncated. Read a referenced section for more.]";
        }
        state.References.Clear();
        // Track only vendor references actually returned to the caller, without interpreting the tree.
        foreach (Match match in Regex.Matches(snapshot, @"\[ref=(?<reference>(?:f\d+)?e\d+)\]", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)))
        { state.References.TryAdd(match.Groups["reference"].Value, 0); }
        return new(Page: state.Info, Snapshot: snapshot);
    }

    private PageState FindPage(string sessionId, string? id)
    {
        if (id is null || !_pages.TryGetValue(id, out var page) || page.Info.SessionId != sessionId || page.Page.IsClosed)
        { throw new ToolFailureException("pageId does not identify a live tab in this chat.", "List this chat's browser tabs and use one of their IDs."); }
        return page;
    }

    private async Task ReceiveFrameAsync(PageState page, ICDPSession cdp, JsonElement? payload)
    {
        if (payload is not { } frame) { return; }
        try
        {
            var metadata = frame.GetProperty("metadata");
            var pointer = page.Pointer;
            var image = new BrowserFrame(Convert.FromBase64String(frame.GetProperty("data").GetString()!), metadata.GetProperty("deviceWidth").GetInt32(), metadata.GetProperty("deviceHeight").GetInt32(), pointer?.Item1, pointer?.Item2, pointer?.Item3 ?? 0);
            PublishFrame(page, image);
            // Chromium stops sending after a few unacknowledged frames.
            await cdp.SendAsync("Page.screencastFrameAck", new() { ["sessionId"] = frame.GetProperty("sessionId").GetInt32() }).ConfigureAwait(false);
        }
        catch (PlaywrightException exception) { if (!page.Page.IsClosed) { _logger.LogWarning("Browser frame acknowledgement failed for {PageId}: {FailureType}.", page.Info.Id, exception.GetType().Name); } }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BrowserFrame> StreamAsync(string sessionId, string pageId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var page = FindPage(sessionId, pageId);
        var key = Guid.NewGuid();
        var frames = Channel.CreateBounded<BrowserFrame>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        // Subscribe and replay atomically, so an older cached image cannot replace a newly published frame.
        lock (page.FrameGate)
        {
            page.Viewers.TryAdd(key, frames);
            if (page.LatestFrame is { } latest) { frames.Writer.TryWrite(latest); }
        }
        try
        {
            // Use the existing action deadline for first paint, then unchanged pages can remain idle indefinitely.
            try
            {
                if (!await frames.Reader.WaitToReadAsync(cancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(PageActionTimeoutMilliseconds), cancellationToken).ConfigureAwait(false)) { yield break; }
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Browser page {PageId} produced no initial frame within {TimeoutMilliseconds} milliseconds.", pageId, PageActionTimeoutMilliseconds);
                throw;
            }
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) { yield return frame; }
        }
        finally { page.Viewers.TryRemove(key, out _); frames.Writer.TryComplete(); }
    }

    /// <summary>Retains genuine page images for reconnects and delivers them in subscription order.</summary>
    private static void PublishFrame(PageState page, BrowserFrame frame)
    {
        lock (page.FrameGate)
        {
            page.LatestFrame = frame;
            foreach (var viewer in page.Viewers.Values) { viewer.Writer.TryWrite(frame); }
        }
    }

    // Send target coordinates through the viewer stream, without injecting presentation into the website.
    private static async Task ShowTargetAsync(PageState page, string? reference)
    {
        var target = ResolveReference(page, reference);
        await target.HoverAsync().ConfigureAwait(false);
        var bounds = await target.BoundingBoxAsync().ConfigureAwait(false);
        if (bounds is null) { return; }
        // Repeated actions on the same element still need distinct visual feedback.
        var pointer = Tuple.Create(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, (page.Pointer?.Item3 ?? 0) + 1);
        page.Pointer = pointer;
        if (page.Viewers.IsEmpty) { return; }
        var viewport = page.Page.ViewportSize!;
        var image = new BrowserFrame(await page.Page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 80 }).ConfigureAwait(false),
            viewport.Width, viewport.Height, pointer.Item1, pointer.Item2, pointer.Item3);
        PublishFrame(page, image);
    }

    private async Task TrackDownloadAsync(PageState page, IDownload download)
    {
        var id = Guid.NewGuid().ToString("N");
        var state = new DownloadState(download, new(id, Path.GetFileName(download.SuggestedFilename), "pending"));
        page.Downloads.TryAdd(id, state);
        PageChanged?.Invoke(this, new(page.Info));
        try
        {
            var error = await download.FailureAsync().ConfigureAwait(false);
            state.Info = state.Info with { State = error is null ? "completed" : "failed", Error = error };
            PageChanged?.Invoke(this, new(page.Info));
        }
        catch (PlaywrightException exception)
        {
            state.Info = state.Info with { State = "failed", Error = "The browser closed before the download completed." };
            _logger.LogWarning("Browser download {DownloadId} failed: {FailureType}.", id, exception.GetType().Name);
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenDownloadAsync(string sessionId, string pageId, string downloadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = FindPage(sessionId, pageId);
        if (!page.Downloads.TryGetValue(downloadId, out var download) || download.Info.State != "completed")
        { throw new ArgumentException("downloadId must identify a completed download in this tab."); }
        return await download.Download.CreateReadStreamAsync().ConfigureAwait(false)
            ?? throw new IOException("The completed download is no longer available.");
    }

    /// <inheritdoc />
    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var page in _pages.Values.Where(page => page.Info.SessionId == sessionId)) { await ClosePageAsync(page).ConfigureAwait(false); }
            if (_privateContexts.Remove(sessionId, out var isolated)) { await isolated.CloseAsync().ConfigureAwait(false); }
            await StopIfEmptyAsync().ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    private async Task StopIfEmptyAsync()
    {
        if (!_pages.IsEmpty) { return; }
        if (_regular is not null) { await _regular.CloseAsync().ConfigureAwait(false); _regular = null; }
        if (_privateBrowser is not null) { await _privateBrowser.CloseAsync().ConfigureAwait(false); _privateBrowser = null; }
        _privateContexts.Clear();
    }

    /// <summary>Lets interrupted website actions observe the completed intentional page closure.</summary>
    private static async Task ClosePageAsync(PageState page)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.CloseCompleted = closed.Task;
        try { await page.Page.CloseAsync().ConfigureAwait(false); }
        finally { closed.TrySetResult(); }
    }

    private async Task StopAfterPageClosedAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try { await StopIfEmptyAsync().ConfigureAwait(false); }
        catch (PlaywrightException exception) { _logger.LogWarning("Could not stop the empty browser runtime: {FailureType}.", exception.GetType().Name); }
        finally { _commands.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (var page in _pages.Values) { await ClosePageAsync(page).ConfigureAwait(false); }
            await StopIfEmptyAsync().ConfigureAwait(false);
            _playwright?.Dispose();
        }
        finally { _commands.Release(); }
    }

    /// <summary>Keeps page-owned observations, transfers and independent viewer queues together.</summary>
    private sealed class PageState(IPage page, ICDPSession cdp, BrowserPage info)
    {
        public SemaphoreSlim Commands { get; } = new(1, 1);
        public Task? CloseCompleted { get; set; }
        public Lock FrameGate { get; } = new();
        public BrowserFrame? LatestFrame { get; set; }
        public IPage Page { get; } = page;
        public ICDPSession Cdp { get; } = cdp;
        public BrowserPage Info { get; set; } = info;
        public IFileChooser? Chooser { get; set; }
        public IElementHandle? Select { get; set; }
        public Tuple<float, float, int>? Pointer { get; set; }
        public ConcurrentDictionary<string, byte> References { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<Guid, Channel<BrowserFrame>> Viewers { get; } = new();
        public ConcurrentDictionary<string, DownloadState> Downloads { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Retains the vendor download handle until its owning page closes.</summary>
    private sealed class DownloadState(IDownload download, BrowserDownload info)
    {
        public IDownload Download { get; } = download;
        public BrowserDownload Info { get; set; } = info;
    }
}
