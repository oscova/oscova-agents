namespace Oscova.Agents.Host.Services.UI;

public class DialogEventArgs : EventArgs
{
    public Type ComponentType { get; set; } = null!;
    public string DialogId { get; set; } = string.Empty;
    public IDictionary<string, object?>? Parameters { get; set; }
    internal Action? OnClosed { get; init; }
}
