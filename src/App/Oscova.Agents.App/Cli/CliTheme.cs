using Spectre.Console;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Single source of colour for every CLI surface so terminals degrade consistently.
/// </summary>
public static class CliTheme
{
    public static readonly Color Accent = Color.Aquamarine1;
    public static readonly Color AccentDim = Color.CadetBlue;
    public static readonly Color Text = Color.Grey93;
    public static readonly Color Muted = Color.Grey58;
    public static readonly Color Success = Color.Green;
    public static readonly Color Warning = Color.Orange1;
    public static readonly Color Danger = Color.Red;

    public static readonly Style Heading = new(Accent, decoration: Decoration.Bold);
    public static readonly Style Label = new(Muted);
    public static readonly Style Value = new(Text);
    public static readonly Style Border = new(AccentDim);

    // Markup equivalents for the few Spectre surfaces that only accept markup, such as panel headers.
    public const string HeadingMarkup = "bold aquamarine1";
    public const string LabelMarkup = "grey58";
}
