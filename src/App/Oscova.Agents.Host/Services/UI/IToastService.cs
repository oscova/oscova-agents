namespace Oscova.Agents.Host.Services.UI;

public interface IToastService
{
    event EventHandler<ToastEventArgs>? ToastShowing;
    void Show(string message);
    void Show(string message, ToastType toastType);
    void Show(string message, ToastType toastType, TimeSpan duration);
}
