using System.ComponentModel;
using System.Text.Json;
using Oscova.Agents.Browsing;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Plugins.Browser;

public sealed partial class BrowserToolset
{
    /// <summary>Discovers and manages browser tabs owned by the active chat.</summary>
    [AgentTool, DisplayName("tabs"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("List, open, close or focus browser tabs in this chat.\nUse action list, open, close or focus.\nclose and focus require pageId from list.\nopen accepts an optional absolute HTTP or HTTPS url of at most 4096 characters.\nAt most 8 tabs can be live across your chats.")]
    public Task<string> Tabs([Description("list, open, close or focus.")] string action = "list",
        [Description("Tab ID from list, required for close or focus.")] string? pageId = null,
        [Description("Optional HTTP or HTTPS URL for open, at most 4096 characters.")] string? url = null,
        CancellationToken cancellationToken = default)
    {
        if (action is not ("list" or "open" or "close" or "focus")) { throw new ToolFailureException("action must be list, open, close or focus."); }
        return RunToolAsync(new(action, pageId, url), cancellationToken);
    }

    /// <summary>Navigates an existing tab and observes its new page.</summary>
    [AgentTool, DisplayName("navigate"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Navigate a tab and return its accessibility snapshot.\npageId must belong to this chat.\nurl must be an absolute HTTP or HTTPS URL of at most 4096 characters.\nNavigation waits at most 30 seconds.")]
    public Task<string> Navigate([Description("Tab ID from tabs.")] string pageId, [Description("Absolute HTTP or HTTPS URL, at most 4096 characters.")] string url, CancellationToken cancellationToken = default)
        => RunToolAsync(new("navigate", pageId, url), cancellationToken);

    /// <summary>Reads current page content and vendor-provided element references.</summary>
    [AgentTool, DisplayName("read"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Read a tab's current accessibility snapshot and element references.\npageId must belong to this chat.\nOptional reference must come from the last observation of this tab.\ndepth must be 1 to 40.\nOutput is limited to 24000 characters. Read a referenced section when truncated.")]
    public Task<string> Read([Description("Tab ID from tabs.")] string pageId, [Description("Optional element reference from this tab's last observation.")] string? reference = null,
        [Description("Snapshot depth, 1 to 40.")] int depth = 20, CancellationToken cancellationToken = default)
        => RunToolAsync(new("read", pageId, Reference: reference, X: depth), cancellationToken);

    /// <summary>Clicks a referenced element and returns a fresh observation.</summary>
    [AgentTool, DisplayName("click"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Click an element and observe the resulting page.\npageId must belong to this chat.\nreference must come from the last observation of this tab.\nAction waits at most 10 seconds.\nIf a reference fails, read the page again.")]
    public Task<string> Click([Description("Tab ID from tabs.")] string pageId, [Description("Element reference from this tab's last observation.")] string reference, CancellationToken cancellationToken = default)
        => RunToolAsync(new("click", pageId, Reference: reference), cancellationToken);

    /// <summary>Replaces an editable field's contents.</summary>
    [AgentTool, DisplayName("fill"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Replace the contents of an editable field.\npageId must belong to this chat.\nreference must come from the last observation of this tab.\ntext may contain at most 16384 characters.\nAn empty text clears the field.\nAction waits at most 10 seconds.")]
    public Task<string> Fill([Description("Tab ID from tabs.")] string pageId, [Description("Editable element reference from this tab's last observation.")] string reference,
        [Description("Replacement text, at most 16384 characters.")] string text, CancellationToken cancellationToken = default)
        => RunToolAsync(new("fill", pageId, text, reference), cancellationToken);

    /// <summary>Presses an editing or navigation key in the current focused element.</summary>
    [AgentTool, DisplayName("press_key"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Press a keyboard key in a tab.\npageId must belong to this chat.\nUse Enter, Tab, Shift+Tab, Escape, Backspace, Delete, Home, End or ArrowLeft/Right/Up/Down.\nEditing combinations support Control+A/Z/Y, Shift+arrows/Home/End, Control+left/right/Home/End/Backspace/Delete and Control+Shift+left/right/Home/End.")]
    public Task<string> PressKey([Description("Tab ID from tabs.")] string pageId, [Description("Supported keyboard key or combination.")] string key, CancellationToken cancellationToken = default)
        => RunToolAsync(new("key", pageId, key), cancellationToken);

    /// <summary>Scrolls the page or a referenced scrollable region.</summary>
    [AgentTool, DisplayName("scroll"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Scroll a tab by CSS pixels and read the page afterward.\npageId must belong to this chat.\nVertical pixels must be -2000 to 2000. Positive values scroll down.\nOptional reference targets a region from this tab's last observation.")]
    public Task<string> Scroll([Description("Tab ID from tabs.")] string pageId, [Description("Vertical CSS pixels, -2000 to 2000.")] int pixels = 650,
        [Description("Optional region reference from this tab's last observation.")] string? reference = null, CancellationToken cancellationToken = default)
    {
        if (pixels is < -2000 or > 2000) { throw new ToolFailureException("pixels must be -2000 to 2000."); }
        return RunToolAsync(new("scroll", pageId, Reference: reference, Y: pixels), cancellationToken);
    }

    /// <summary>Selects values in a native option control.</summary>
    [AgentTool, DisplayName("select_option"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Select option values in a native select control.\npageId must belong to this chat.\nreference must come from the last observation of this tab.\nvalues must contain 1 to 32 option values.\nAction waits at most 10 seconds.")]
    public Task<string> SelectOption([Description("Tab ID from tabs.")] string pageId, [Description("Select element reference.")] string reference,
        [Description("1 to 32 option values.")] string[] values, CancellationToken cancellationToken = default)
        => RunToolAsync(new("select", pageId, Reference: reference, Values: values), cancellationToken);

    /// <summary>Navigates history or reloads an existing tab.</summary>
    [AgentTool, DisplayName("history"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Navigate a tab's history or reload it.\npageId must belong to this chat.\naction must be back, forward or reload.\nNavigation waits at most 30 seconds.")]
    public Task<string> History([Description("Tab ID from tabs.")] string pageId, [Description("back, forward or reload.")] string action, CancellationToken cancellationToken = default)
    {
        if (action is not ("back" or "forward" or "reload")) { throw new ToolFailureException("action must be back, forward or reload."); }
        return RunToolAsync(new(action, pageId), cancellationToken);
    }

    /// <summary>Waits for page text without requiring a model-written polling loop.</summary>
    [AgentTool, DisplayName("wait_for_text"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Wait for visible text in a tab.\npageId must belong to this chat.\ntext must contain 1 to 1024 characters.\nseconds must be 1 to 30.")]
    public Task<string> WaitForText([Description("Tab ID from tabs.")] string pageId, [Description("Visible text, 1 to 1024 characters.")] string text,
        [Description("Timeout in seconds, 1 to 30.")] int seconds = 10, CancellationToken cancellationToken = default)
        => RunToolAsync(new("wait", pageId, text, X: seconds), cancellationToken);

    /// <summary>Uploads files authorized by the active run's existing directory grants.</summary>
    [AgentTool, DisplayName("upload"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("Upload files to a website.\npageId must belong to this chat.\npaths must contain 1 to 8 readable files in this run's allowed directories.\nCombined file size must not exceed 32 MiB.\nreference must identify a file input from the last observation, or omit it when a website file chooser is open.\nObtain user confirmation before disclosing sensitive files.")]
    public async Task<string> Upload([Description("Tab ID from tabs.")] string pageId, [Description("1 to 8 authorized agent-facing file paths, at most 32 MiB total.")] string[] paths,
        [Description("File-input reference, or omit for an open file chooser.")] string? reference = null, CancellationToken cancellationToken = default)
    {
        await ResolveSessionAsync(cancellationToken).ConfigureAwait(false);
        if (paths.Length is < 1 or > 8) { throw new ToolFailureException("paths must contain 1 to 8 files."); }
        List<BrowserUpload> uploads = [];
        var files = new WorkspaceFileAccess(context.GetAllowedDirectories);
        long total = 0;
        foreach (var path in paths)
        {
            // Authorization, sizing and reads share one held file handle.
            await using var input = files.OpenRead(path);
            var length = input.Length;
            total += length;
            if (total > 32 * 1024 * 1024) { throw new ToolFailureException("Combined upload size exceeds 32 MiB."); }
            var data = new byte[checked((int)length)];
            await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            uploads.Add(new(Path.GetFileName(path), "application/octet-stream", data));
        }
        return await RunToolAsync(new("upload", pageId, Reference: reference, Files: uploads), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists website downloads or saves one into an authorized writable directory.</summary>
    [AgentTool, DisplayName("downloads"), ToolNetworkScope(NetworkScope.Egress)]
    [Description("List a tab's downloads or save a completed download.\npageId must belong to this chat.\nTo save, supply downloadId from the list and a new destination file in this run's writable directories.\nExisting files are never overwritten.\nDownloaded files must not exceed 32 MiB.")]
    public async Task<string> Downloads([Description("Tab ID from tabs.")] string pageId, [Description("Completed download ID, or omit to list.")] string? downloadId = null,
        [Description("New writable agent-facing file path, required with downloadId.")] string? destination = null, CancellationToken cancellationToken = default)
    {
        if (downloadId is null) { return await RunToolAsync(new("downloads", pageId), cancellationToken).ConfigureAwait(false); }
        var session = await ResolveSessionAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(destination)) { throw new ToolFailureException("destination is required when saving a download."); }
        _ = PathHelper.ResolveAuthorizedHostPath(destination, context.GetAllowedDirectories(), WorkspacePathAccess.Write);
        await using var input = await OpenDownloadAsync(session.Id, pageId, downloadId, cancellationToken).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (data.Length + read > 32 * 1024 * 1024) { throw new ToolFailureException("Download exceeds 32 MiB."); }
            data.Write(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Revalidate the original agent path and create through a held parent without replacing existing files.
        new WorkspaceFileAccess(context.GetAllowedDirectories).WriteNew(destination, data.WriteTo);
        return JsonSerializer.Serialize(new { path = destination, bytes = data.Length });
    }

    private Task<BrowserSessionScope> ResolveSessionAsync(CancellationToken cancellationToken)
        => context.ResolveBrowserSessionAsync?.Invoke(cancellationToken)
            ?? throw new ToolFailureException("This host does not provide browser access for the current chat.");

    private async Task<string> RunToolAsync(BrowserCommand command, CancellationToken cancellationToken)
    {
        // Resolve trusted ownership and effective network policy before even listing tabs.
        await ResolveSessionAsync(cancellationToken).ConfigureAwait(false);
        var execute = context.ExecuteBrowserCommandAsync
            ?? throw new ToolFailureException("This host does not provide browser commands for the current chat.");
        try
        {
            var result = await execute(command, cancellationToken).ConfigureAwait(false);
            // A closed-page result describes an intentional lifecycle action, not a navigation failure.
            if (result.Pages is null && result.Text is { } failure && command.Kind is "open" or "navigate" or "back" or "forward" or "reload")
            { throw new ToolFailureException(failure, "Check the tab's address and the browser host's network or DNS connection before retrying."); }
            // Agent actions need fresh references. Human viewers already receive the rendered page stream.
            if (result.Pages is null && command.Kind is "open" or "navigate" or "click" or "fill" or "select" or "back" or "forward" or "reload" or "wait" or "upload" or "key" or "scroll")
            { result = await execute(new("read", result.Page?.Id ?? command.PageId), cancellationToken).ConfigureAwait(false); }
            return JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (ArgumentException exception) { throw new ToolFailureException(exception.Message, "Correct the named input and retry."); }
    }
}
