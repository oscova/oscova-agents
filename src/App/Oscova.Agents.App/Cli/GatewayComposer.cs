using System.Text;
using Spectre.Console;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Reads one gateway prompt with in-memory history recall, slash-command completion, and an in-place redraw.
/// </summary>
public sealed class GatewayComposer(IReadOnlyList<SlashCommand> commands)
{
    private const string Prompt = "> ";
    private const int MaxSuggestions = 6;
    private const int MinimumWidth = 24;

    // Prompts can hold secrets, so recall never outlives the gateway session.
    private readonly List<string> _history = [];

    /// <summary>
    /// Renders the footer, then reads one line. Returns null when the session should end.
    /// </summary>
    public async Task<string?> ReadAsync(GatewayFooterState footer, CancellationToken cancellationToken)
    {
        var console = AnsiConsole.Console;

        // Key-level editing needs both a live terminal and cursor control; anything else reads a plain
        // line so piped input and redirected output stay byte-clean. Redirected input never reaches
        // here because GatewayCommand does not open a session at all in that case.
        if (!console.Profile.Capabilities.Interactive || !console.Profile.Capabilities.Ansi)
        {
            return await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        RenderFooter(console, footer);
        return await EditAsync(console, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> EditAsync(IAnsiConsole console, CancellationToken cancellationToken)
    {
        var entries = _history;
        // One past the last entry means "not recalling"; that slot holds the draft the user typed.
        var historyIndex = entries.Count;
        var draft = string.Empty;
        var buffer = new StringBuilder();
        var caret = 0;
        var highlighted = 0;
        var frameLines = 0;

        while (true)
        {
            var matches = Matches(buffer.ToString());
            highlighted = matches.Count == 0 ? 0 : Math.Clamp(highlighted, 0, matches.Count - 1);
            frameLines = RenderFrame(console, buffer.ToString(), caret, matches, highlighted, frameLines);

            var pressed = await console.Input.ReadKeyAsync(intercept: true, cancellationToken).ConfigureAwait(false);
            if (pressed is null)
            {
                CloseFrame(console, buffer.ToString(), frameLines);
                return null;
            }

            var key = pressed.Value;
            var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);

            if (key.Key == ConsoleKey.Enter)
            {
                CloseFrame(console, buffer.ToString(), frameLines);
                var line = buffer.ToString();
                Remember(line);
                return line;
            }

            // Ctrl+C abandons what is typed; on an empty line, like Ctrl+D, it ends the session.
            if (control && key.Key is ConsoleKey.C or ConsoleKey.D)
            {
                if (buffer.Length == 0)
                {
                    CloseFrame(console, string.Empty, frameLines);
                    return null;
                }

                if (key.Key == ConsoleKey.C)
                {
                    CloseFrame(console, buffer.ToString(), frameLines);
                    return string.Empty;
                }

                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.Tab when matches.Count > 0:
                    buffer.Clear().Append(matches[highlighted].Name);
                    caret = buffer.Length;
                    continue;
                case ConsoleKey.UpArrow when matches.Count > 0:
                    highlighted = highlighted == 0 ? matches.Count - 1 : highlighted - 1;
                    continue;
                case ConsoleKey.DownArrow when matches.Count > 0:
                    highlighted = highlighted == matches.Count - 1 ? 0 : highlighted + 1;
                    continue;
                case ConsoleKey.UpArrow when historyIndex > 0:
                    if (historyIndex == entries.Count)
                    {
                        draft = buffer.ToString();
                    }

                    historyIndex--;
                    buffer.Clear().Append(entries[historyIndex]);
                    caret = buffer.Length;
                    continue;
                case ConsoleKey.DownArrow when historyIndex < entries.Count:
                    historyIndex++;
                    buffer.Clear().Append(historyIndex == entries.Count ? draft : entries[historyIndex]);
                    caret = buffer.Length;
                    continue;
                case ConsoleKey.Backspace when caret > 0:
                    buffer.Remove(caret - 1, 1);
                    caret--;
                    continue;
                case ConsoleKey.Delete when caret < buffer.Length:
                    buffer.Remove(caret, 1);
                    continue;
                case ConsoleKey.LeftArrow when caret > 0:
                    caret--;
                    continue;
                case ConsoleKey.RightArrow when caret < buffer.Length:
                    caret++;
                    continue;
                case ConsoleKey.Home:
                    caret = 0;
                    continue;
                case ConsoleKey.End:
                    caret = buffer.Length;
                    continue;
            }

            if (!control && !char.IsControl(key.KeyChar))
            {
                buffer.Insert(caret, key.KeyChar);
                caret++;
                highlighted = 0;
            }
        }
    }

    private IReadOnlyList<SlashCommand> Matches(string text)
    {
        if (!text.StartsWith('/'))
        {
            return [];
        }

        var matched = new List<SlashCommand>();
        foreach (var command in commands)
        {
            if (command.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(command.Name, text, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(command);
            }

            if (matched.Count == MaxSuggestions)
            {
                break;
            }
        }

        return matched;
    }

    private static void RenderFooter(IAnsiConsole console, GatewayFooterState footer)
    {
        var parts = new List<string>
        {
            $"host {footer.HostUrl}",
            $"agent {footer.AgentName}",
            $"model {footer.ModelName}"
        };
        if (!string.IsNullOrWhiteSpace(footer.ThreadTitle))
        {
            parts.Add($"thread {footer.ThreadTitle}");
        }

        var width = ContentWidth(console);
        var line = string.Join(" - ", parts);
        console.Write(new Text(line.Length > width ? line[..width] : line, CliTheme.Label));
        console.WriteLine();
    }

    /// <summary>
    /// Draws the input line and the suggestion popup over the previous frame, then parks the caret.
    /// </summary>
    private static int RenderFrame(
        IAnsiConsole console,
        string text,
        int caret,
        IReadOnlyList<SlashCommand> matches,
        int highlighted,
        int previousLines)
    {
        var width = ContentWidth(console);
        var (visible, caretColumn) = Window(text, caret, width);

        var lines = new List<(string Text, Style Style)> { (Prompt + visible, CliTheme.Value) };
        for (var index = 0; index < matches.Count; index++)
        {
            var style = index == highlighted ? CliTheme.Heading : CliTheme.Label;
            lines.Add(($"  {matches[index].Name}  {matches[index].Description}", style));
        }

        // Each completed frame already parks the cursor on the input row.
        var rows = Math.Max(lines.Count, previousLines);
        for (var row = 0; row < rows; row++)
        {
            console.Cursor.Move(CursorDirection.Left, width + Prompt.Length);
            var content = row < lines.Count ? lines[row] : (Text: string.Empty, Style: CliTheme.Value);
            console.Write(new Text(Fit(content.Text, width), content.Style));
            if (row < rows - 1)
            {
                console.WriteLine();
            }
        }

        // Over-moving left is safe: the terminal clamps at column 0, which is the anchor we count from.
        MoveUp(console, rows - 1);
        console.Cursor.Move(CursorDirection.Left, width + Prompt.Length);
        console.Cursor.Move(CursorDirection.Right, caretColumn);
        return lines.Count;
    }

    /// <summary>
    /// Leaves the composer by clearing the popup, keeping the submitted line, and moving below it.
    /// </summary>
    private static void CloseFrame(IAnsiConsole console, string text, int frameLines)
    {
        RenderFrame(console, text, text.Length, matches: [], highlighted: 0, previousLines: frameLines);
        console.WriteLine();
    }

    private static void MoveUp(IAnsiConsole console, int steps)
    {
        if (steps > 0)
        {
            console.Cursor.Move(CursorDirection.Up, steps);
        }
    }

    // One column short of the real width: writing into the last column makes the terminal auto-wrap,
    // which would push the frame down a line on every redraw.
    private static int ContentWidth(IAnsiConsole console) => Math.Max(MinimumWidth, console.Profile.Width) - 1;

    /// <summary>
    /// Slides a window over the buffer so the caret stays visible on a line narrower than the text.
    /// </summary>
    private static (string Visible, int CaretColumn) Window(string text, int caret, int width)
    {
        var room = Math.Max(1, width - Prompt.Length);
        if (text.Length <= room)
        {
            return (text, Prompt.Length + caret);
        }

        var offset = Math.Clamp(caret - room + 1, 0, text.Length - room);
        return (text.Substring(offset, room), Prompt.Length + caret - offset);
    }

    // Padding to the full width is what erases the previous frame; Spectre has no erase-line helper.
    private static string Fit(string text, int width) =>
        text.Length >= width ? text[..width] : text.PadRight(width);

    // Blank prompts and an immediate repeat are not recalled.
    private void Remember(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || (_history.Count > 0 && string.Equals(_history[^1], trimmed, StringComparison.Ordinal)))
        {
            return;
        }

        _history.Add(trimmed);
    }
}

/// <summary>
/// One slash command offered by the composer's suggestion popup.
/// </summary>
public sealed record SlashCommand(string Name, string Description);

/// <summary>
/// The single dim line drawn above the composer describing where the next turn goes.
/// </summary>
public sealed record GatewayFooterState(string HostUrl, string AgentName, string ModelName, string? ThreadTitle);
