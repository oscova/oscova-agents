namespace Oscova.Agents.Host.Services.UI;

public static class ToastExtensions
{
    public static string ToCssClass(this ToastType toastType)
    {
        return toastType switch
        {
            ToastType.Info => "alert-info",
            ToastType.Success => "alert-success",
            ToastType.Warning => "alert-warning",
            ToastType.Error => "alert-error",
            _ => "alert-info",
        };
    }

    public static string ToCssClass(this ToastHorizontalPosition horizontalPosition)
    {
        return horizontalPosition switch
        {
            ToastHorizontalPosition.Start => "toast-start",
            ToastHorizontalPosition.Center => "toast-center",
            ToastHorizontalPosition.End => "toast-end",
            _ => "toast-center",
        };
    }

    public static string ToCssClass(this ToastVerticalPosition verticalPosition)
    {
        return verticalPosition switch
        {
            ToastVerticalPosition.Top => "toast-top",
            ToastVerticalPosition.Middle => "toast-middle",
            ToastVerticalPosition.Bottom => "toast-bottom",
            _ => "toast-top",
        };
    }

    public static string ToSvg(this ToastType toastType)
    {
        return toastType switch
        {
            ToastType.Info => "<svg xmlns=\"http://www.w3.org/2000/svg\" fill=\"none\" viewBox=\"0 0 24 24\" class=\"stroke-current shrink-0 w-6 h-6\"><path stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke-width=\"2\" d=\"M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z\"></path></svg>",
            ToastType.Success => "<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"stroke-current shrink-0 h-6 w-6\" fill=\"none\" viewBox=\"0 0 24 24\"><path stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke-width=\"2\" d=\"M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z\" /></svg>",
            ToastType.Warning => "<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"stroke-current shrink-0 h-6 w-6\" fill=\"none\" viewBox=\"0 0 24 24\"><path stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke-width=\"2\" d=\"M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z\" /></svg>",
            ToastType.Error => "<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"stroke-current shrink-0 h-6 w-6\" fill=\"none\" viewBox=\"0 0 24 24\"><path stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke-width=\"2\" d=\"M10 14l2-2m0 0l2-2m-2 2l-2-2m2 2l2 2m7-2a9 9 0 11-18 0 9 9 0 0118 0z\" /></svg>",
            _ => "<svg xmlns=\"http://www.w3.org/2000/svg\" fill=\"none\" viewBox=\"0 0 24 24\" class=\"stroke-current shrink-0 w-6 h-6\"><path stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke-width=\"2\" d=\"M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z\"></path></svg>"
        };
    }
}
