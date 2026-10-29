namespace Oscova.Agents.Host.Services.UI;

public class ToastEventArgs : EventArgs
{
    public string Message { get; set; } = string.Empty;
    public ToastType Type { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(4);
    public DateTime CreatedOn { get; set; } = DateTime.Now;
}
