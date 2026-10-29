using System.Text;
using Spectre.Console;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Renders the Oscova wordmark for interactive terminal entry points.
/// </summary>
public static class CliBranding
{
    private const int FrameDelayMilliseconds = 75;
    private const int RevealStepColumns = 6;
    private const char SolidBlock = '\u2588';
    private const char LightShade = '\u2591';

    /// <summary>
    /// Renders the animated wordmark when the terminal supports it, otherwise a static header.
    /// </summary>
    public static async Task RenderIntroAsync(OscovaCommandSettings settings, CancellationToken cancellationToken)
    {
        EnsureUtf8Output();
        if (!ShouldRenderRichIntro(settings))
        {
            RenderStaticHeader(settings);
            return;
        }

        var frames = BuildRevealFrames(FinalWordmark);
        await AnsiConsole.Live(BuildHeader(frames[0]))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                foreach (var frame in frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ctx.UpdateTarget(BuildHeader(frame));
                    await Task.Delay(FrameDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders a non-animated header for static terminal entry points.
    /// </summary>
    public static void RenderStaticHeader(OscovaCommandSettings settings)
    {
        if (settings.Json || settings.Quiet || Console.IsOutputRedirected)
        {
            return;
        }

        if (!CanRenderUnicodeAnsi(settings))
        {
            AnsiConsole.WriteLine("Oscova Agents");
            return;
        }

        AnsiConsole.Write(BuildHeader(FinalWordmark));
    }

    private static void EnsureUtf8Output()
    {
        if (!Console.IsOutputRedirected && !Console.OutputEncoding.Equals(Encoding.UTF8))
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool ShouldRenderRichIntro(OscovaCommandSettings settings) =>
        CanRenderUnicodeAnsi(settings) &&
        !settings.NoInteractive &&
        AnsiConsole.Profile.Capabilities.Interactive;

    private static bool CanRenderUnicodeAnsi(OscovaCommandSettings settings) =>
        !settings.Json &&
        !settings.Quiet &&
        !settings.NoColor &&
        !Console.IsOutputRedirected &&
        Environment.GetEnvironmentVariable("NO_COLOR") is null &&
        AnsiConsole.Profile.Capabilities.Ansi &&
        AnsiConsole.Profile.Capabilities.Unicode;

    private static Panel BuildHeader(string[] wordmark)
    {
        var rows = new Rows(
            wordmark.Select(line => new Text(line, CliTheme.Value)).ToArray());

        var panel = new Panel(rows)
            .Border(BoxBorder.Rounded)
            .BorderStyle(CliTheme.Border)
            .Padding(1, 0);
        panel.Header = new PanelHeader($"[{CliTheme.LabelMarkup}]Oscova Agents[/]", Justify.Center);
        return panel;
    }

    private static IReadOnlyList<string[]> BuildRevealFrames(string[] finalWordmark)
    {
        var width = finalWordmark.Max(line => line.Length);
        var frames = new List<string[]>();

        for (var visibleColumns = RevealStepColumns; visibleColumns < width; visibleColumns += RevealStepColumns)
        {
            frames.Add(
                finalWordmark
                    .Select(line => RevealLine(line.PadRight(width), visibleColumns))
                    .ToArray());
        }

        frames.Add(finalWordmark);
        return frames;
    }

    private static string RevealLine(string line, int visibleColumns)
    {
        var chars = line.ToCharArray();
        for (var index = visibleColumns; index < chars.Length; index++)
        {
            chars[index] = chars[index] == ' ' ? ' ' : LightShade;
        }

        return new string(chars);
    }

    private static string[] BuildWordmark() =>
    [
        $" {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}   {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}   {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}   {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}   {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock} ",
        $"{SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock} {SolidBlock}{SolidBlock}     {SolidBlock}{SolidBlock}     {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock} {SolidBlock}{SolidBlock}   {SolidBlock}{SolidBlock} {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}",
        $"{SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}{SolidBlock}   {SolidBlock}{SolidBlock}     {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock} {SolidBlock}{SolidBlock}   {SolidBlock}{SolidBlock} {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}",
        $"{SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}    {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}     {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock} {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}",
        $" {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}    {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}   {SolidBlock}{SolidBlock}{SolidBlock}{SolidBlock}    {SolidBlock}{SolidBlock}{SolidBlock}   {SolidBlock}{SolidBlock}  {SolidBlock}{SolidBlock}"
    ];

    private static readonly string[] FinalWordmark = BuildWordmark();
}
