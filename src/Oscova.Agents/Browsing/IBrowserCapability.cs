using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Browsing;

/// <summary>Shares a plugin's browser runtime with its host without exposing a browser-library dependency.</summary>
public interface IBrowserCapability : IAsyncDisposable
{
    /// <summary>Reports page state and explicit focus requests within the owning session.</summary>
    event EventHandler<BrowserPageChange>? PageChanged;

    /// <summary>Lists live pages without starting the browser.</summary>
    IReadOnlyList<BrowserPage> GetPages(string sessionId);

    /// <summary>Executes a browser action in a host-authorized session.</summary>
    Task<BrowserResult> ExecuteAsync(BrowserSessionScope session, BrowserCommand command, CancellationToken cancellationToken = default);

    /// <summary>Streams the newest image independently to each viewer of an owned page.</summary>
    IAsyncEnumerable<BrowserFrame> StreamAsync(string sessionId, string pageId, CancellationToken cancellationToken = default);

    /// <summary>Opens a completed download, returning a stream the caller must dispose.</summary>
    Task<Stream> OpenDownloadAsync(string sessionId, string pageId, string downloadId, CancellationToken cancellationToken = default);

    /// <summary>Closes the session's pages and releases its isolated state.</summary>
    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Carries session ownership resolved by the host for the current invocation.</summary>
public sealed record BrowserSessionScope(string Id, bool IsEphemeral, string? SelectedPageId = null);

/// <summary>Identifies a live browser page within its owning session.</summary>
public sealed record BrowserPage(string Id, string SessionId, string Url, string Title, string? OpenerId = null, bool HasFileChooser = false,
    bool CanGoBack = false, bool CanGoForward = false);

/// <summary>Describes a page update, closure or requested focus without prescribing host presentation.</summary>
public sealed class BrowserPageChange(BrowserPage page, bool closed = false, bool focusRequested = false, string? error = null) : EventArgs
{
    /// <summary>Gets the page whose state changed.</summary>
    public BrowserPage Page { get; } = page;
    /// <summary>Gets whether the page has closed.</summary>
    public bool Closed { get; } = closed;
    /// <summary>Gets whether the action requests that the host reveal this page.</summary>
    public bool FocusRequested { get; } = focusRequested;
    /// <summary>Gets actionable feedback for a website event that could not complete.</summary>
    public string? Error { get; } = error;
}

/// <summary>Carries a browser action shared by the plugin's tools and the host viewer.</summary>
public sealed record BrowserCommand(string Kind, string? PageId = null, string? Text = null,
    string? Reference = null, float X = 0, float Y = 0, int ClickCount = 1,
    IReadOnlyList<string>? Values = null, IReadOnlyList<BrowserUpload>? Files = null);

/// <summary>Returns page observations and the small responses required by a streamed viewer.</summary>
public sealed record BrowserResult(BrowserPage? Page = null, IReadOnlyList<BrowserPage>? Pages = null,
    string? Snapshot = null, string? Text = null, string? Cursor = null, IReadOnlyList<BrowserDownload>? Downloads = null,
    BrowserSelect? Select = null);

/// <summary>Exposes a native option popup that cannot appear in a page image.</summary>
public sealed record BrowserSelect(string Label, IReadOnlyList<BrowserOption> Options);

/// <summary>Preserves the label and selection state of a website option.</summary>
public sealed record BrowserOption(string Label, bool Disabled, bool Selected);

/// <summary>Carries a rendered page and its optional agent target in viewport CSS pixels.</summary>
public sealed record BrowserFrame(ReadOnlyMemory<byte> Jpeg, int Width, int Height, float? PointerX, float? PointerY, int PointerRevision);

/// <summary>Supplies caller-authorized file contents to a website.</summary>
public sealed record BrowserUpload(string Name, string ContentType, ReadOnlyMemory<byte> Data);

/// <summary>Identifies a website download and its completion state within one page.</summary>
public sealed record BrowserDownload(string Id, string Name, string State, string? Error = null);
