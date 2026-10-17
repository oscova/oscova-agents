using Microsoft.Playwright;
using System.Text.Json;
using Oscova.Agents.Browsing;

namespace Oscova.Agents.Plugins.Browser;

public sealed partial class BrowserToolset
{
    // Pointer coordinates are CSS pixels, independent of the viewer's displayed image size.
    private static async Task<BrowserResult?> SendInputAsync(PageState state, BrowserCommand command)
    {
        var page = state.Page;
        if (!float.IsFinite(command.X) || !float.IsFinite(command.Y)) { throw new ArgumentException("Input coordinates must be finite numbers."); }
        switch (command.Kind)
        {
                case "move":
                case "down":
                case "up":
                    // A frame can precede a resize. Chromium accepts positions outside the current viewport.
                    if (command.X is < 0 or > 1920 || command.Y is < 0 or > 1080)
                    { throw new ArgumentException("Pointer X must be 0 to 1920 and Y must be 0 to 1080."); }
                    await page.Mouse.MoveAsync(command.X, command.Y).ConfigureAwait(false);
                    if (command.Kind == "down") { await page.Mouse.DownAsync(new() { ClickCount = Math.Clamp(command.ClickCount, 1, 3) }).ConfigureAwait(false); }
                    if (command.Kind == "up")
                    {
                        await page.Mouse.UpAsync(new() { ClickCount = Math.Clamp(command.ClickCount, 1, 3) }).ConfigureAwait(false);
                        if (page.IsClosed) { return null; }
                        if (state.Select is not null) { await state.Select.DisposeAsync().ConfigureAwait(false); state.Select = null; }
                        // Native dropdown windows are absent from screencast frames. Keep the real control for the viewer's choice.
                        var handle = await page.EvaluateHandleAsync("""
                            ([x, y]) => {
                                let e = document.elementFromPoint(x, y);
                                while (e?.shadowRoot) {
                                    const child = e.shadowRoot.elementFromPoint(x, y);
                                    if (!child || child === e) { break; }
                                    e = child;
                                }
                                return e instanceof HTMLSelectElement && !e.disabled && !e.multiple && e.size <= 1 ? e : null;
                            }
                            """, new double[] { command.X, command.Y }).ConfigureAwait(false);
                        state.Select = handle as IElementHandle;
                        if (state.Select is null) { await handle.DisposeAsync().ConfigureAwait(false); }
                        else
                        {
                            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
                            var options = await state.Select.EvaluateAsync<JsonElement>("""
                                e => ({ label: e.getAttribute('aria-label') || e.labels?.[0]?.textContent || e.name || '',
                                    options: Array.from(e.options, o => ({label: o.label, disabled: o.disabled || o.parentElement.disabled === true, selected: o.selected})) })
                                """).ConfigureAwait(false);
                            return new BrowserResult(Select: options.Deserialize<BrowserSelect>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                        }
                    }
                    if (command.Kind != "move") { return null; }
                    if (page.Url == "chrome-error://chromewebdata/") { return new BrowserResult(Cursor: "default"); }
                    // Pixels carry no CSS cursor, return the style of the element under the remote pointer.
                    try
                    {
                    return new BrowserResult(Cursor: await page.EvaluateAsync<string>("""
                        ([x, y]) => {
                            let element = document.elementFromPoint(x, y);
                            while (element?.shadowRoot) {
                                const child = element.shadowRoot.elementFromPoint(x, y);
                                if (!child || child === element) { break; }
                                element = child;
                            }
                            if (!element) { return 'default'; }
                            const cursor = getComputedStyle(element).cursor;
                            if (cursor !== 'auto') { return cursor.includes('url(') ? 'default' : cursor; }
                            if (element.closest('a[href]')) { return 'pointer'; }
                            return element.matches('input:not([type=button]):not([type=checkbox]):not([type=range]),textarea,[contenteditable=true]') ? 'text' : 'default';
                        }
                        """, new double[] { command.X, command.Y }).ConfigureAwait(false));
                    }
                    catch (PlaywrightException exception) when (exception.Message.Contains("Execution context was destroyed", StringComparison.Ordinal))
                    {
                        // Navigation can replace the document between moving the pointer and reading its style.
                        return new BrowserResult(Cursor: "default");
                    }
                case "choose":
                    if (state.Select is null || !int.TryParse(command.Text, out var index) || index < 0)
                    { throw new ArgumentException("Open a dropdown and choose one of its options."); }
                    if (!await state.Select.EvaluateAsync<bool>("(e, i) => e.isConnected && !e.disabled && !!e.options[i] && !e.options[i].disabled && !e.options[i].parentElement.disabled", index).ConfigureAwait(false))
                    { throw new ArgumentException("The dropdown option is no longer available. Open the dropdown again."); }
                    await state.Select.SelectOptionAsync(new SelectOptionValue { Index = index }).ConfigureAwait(false);
                    await state.Select.DisposeAsync().ConfigureAwait(false);
                    state.Select = null;
                    break;
                case "selection":
                case "cut":
                    // Only remove the selection after the viewer has successfully copied it.
                    return new BrowserResult(Text: await page.EvaluateAsync<string>("""
                        expected => {
                            let element = document.activeElement;
                            while (element?.shadowRoot?.activeElement) { element = element.shadowRoot.activeElement; }
                            if (element?.matches('input[type=password]')) { throw new Error('Password copying is not supported.'); }
                            const field = element?.matches('input,textarea') && element.selectionStart !== null;
                            const text = field ? element.value.slice(element.selectionStart, element.selectionEnd) : getSelection().toString();
                            if (text.length > 4096) { throw new Error('Select at most 4096 characters.'); }
                            if (expected !== null) {
                                if (!text || text !== expected || !(field || element?.isContentEditable) || element.readOnly || element.disabled) {
                                    throw new Error('The editable selection changed. Select the text again.');
                                }
                                // Chromium's editing command deletes atomically and retains its native undo history.
                                if (!document.execCommand('delete')) { throw new Error('The page rejected deletion.'); }
                            }
                            return text;
                        }
                        """, command.Kind == "cut" ? command.Text ?? "" : null).ConfigureAwait(false));
                case "text":
                    if (command.Text is null || command.Text.Length > 4096) { throw new ArgumentException("Text must contain at most 4096 characters."); }
                    await page.Keyboard.InsertTextAsync(command.Text).ConfigureAwait(false);
                    break;
                case "leave":
                    await page.Mouse.MoveAsync(-1, -1).ConfigureAwait(false);
                    return new BrowserResult(Cursor: "default");
                case "key":
                    if (command.Text is not ("Backspace" or "Delete" or "Enter" or "Tab" or "Shift+Tab" or "Escape" or "ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown" or "Home" or "End" or "Control+A"
                        or "Shift+ArrowLeft" or "Shift+ArrowRight" or "Shift+ArrowUp" or "Shift+ArrowDown" or "Shift+Home" or "Shift+End"
                        or "Control+ArrowLeft" or "Control+ArrowRight" or "Control+Backspace" or "Control+Delete" or "Control+Home" or "Control+End"
                        or "Control+Shift+ArrowLeft" or "Control+Shift+ArrowRight" or "Control+Shift+Home" or "Control+Shift+End" or "Control+Z" or "Control+Y"))
                    { throw new ArgumentException("Unsupported keyboard shortcut."); }
                    // Viewer and tool shortcuts describe editing intent, independent of the browser host's OS.
                    var key = OperatingSystem.IsMacOS() ? command.Text switch
                    {
                        "Control+A" => "Meta+A", "Control+Z" => "Meta+Z", "Control+Y" => "Meta+Shift+Z",
                        "Home" => "Meta+ArrowLeft", "End" => "Meta+ArrowRight",
                        "Shift+Home" => "Meta+Shift+ArrowLeft", "Shift+End" => "Meta+Shift+ArrowRight",
                        "Control+Home" => "Meta+ArrowUp", "Control+End" => "Meta+ArrowDown",
                        "Control+Shift+Home" => "Meta+Shift+ArrowUp", "Control+Shift+End" => "Meta+Shift+ArrowDown",
                        "Control+ArrowLeft" => "Alt+ArrowLeft", "Control+ArrowRight" => "Alt+ArrowRight",
                        "Control+Shift+ArrowLeft" => "Alt+Shift+ArrowLeft", "Control+Shift+ArrowRight" => "Alt+Shift+ArrowRight",
                        "Control+Backspace" => "Alt+Backspace", "Control+Delete" => "Alt+Delete",
                        _ => command.Text
                    } : command.Text;
                    await page.Keyboard.PressAsync(key).ConfigureAwait(false);
                    break;
                case "scroll":
                    await page.Mouse.WheelAsync(Math.Clamp(command.X, -2000, 2000), Math.Clamp(command.Y, -2000, 2000)).ConfigureAwait(false);
                    break;
                case "resize":
                    await page.SetViewportSizeAsync(Math.Clamp((int)command.X, 320, 1920), Math.Clamp((int)command.Y, 240, 1080)).ConfigureAwait(false);
                    break;
            default: throw new ArgumentException("Unknown browser command.");
        }
        return null;
    }
}
