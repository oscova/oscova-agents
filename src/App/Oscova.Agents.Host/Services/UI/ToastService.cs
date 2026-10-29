namespace Oscova.Agents.Host.Services.UI;

public class ToastService : IToastService
{
    private static readonly TimeSpan DefaultToastTimeout = TimeSpan.FromSeconds(4);

    public void Show(string message)
    {
        ToastShowing?.Invoke(this, new ToastEventArgs { Message = message, Type = ToastType.Info, Duration = DefaultToastTimeout });
    }

    public void Show(string message, ToastType toastType)
    {
        ToastShowing?.Invoke(this, new ToastEventArgs { Message = message, Type = toastType, Duration = DefaultToastTimeout });
    }

    public void Show(string message, ToastType toastType, TimeSpan duration)
    {
        ToastShowing?.Invoke(this, new ToastEventArgs { Message = message, Type = toastType, Duration = duration });
    }

    public event EventHandler<ToastEventArgs>? ToastShowing;
}
