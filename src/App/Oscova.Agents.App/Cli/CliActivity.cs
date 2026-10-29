using Spectre.Console;

namespace Oscova.Agents.App.Cli;

/// <summary>Holds a status display until startup or chat yields the console.</summary>
internal sealed class CliActivity
{
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _render;

    public CliActivity(bool enabled, string description)
    {
        _render = enabled
            ? AnsiConsole.Status().StartAsync(description.EscapeMarkup(), _ => _release.Task)
            : Task.CompletedTask;
    }

    /// <summary>Releases and joins the display before another console owner starts.</summary>
    public Task StopAsync()
    {
        _release.TrySetResult();
        return _render;
    }
}
