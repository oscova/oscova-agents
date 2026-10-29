using Microsoft.AspNetCore.Components;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Components.Settings;

/// <summary>
/// Base class for settings tabs and dialogs. Use <see cref="RunAsync"/> to handle any operation that needs a loading flag, success toast, and error toast without repeating the boilerplate.
/// </summary>
public abstract class SettingsComponentBase : ComponentBase
{
    [Inject] protected IToastService ToastService { get; set; } = default!;

    /// <summary>
    /// Runs <paramref name="operation"/>, toggling <paramref name="setFlag"/> and calling <see cref="ComponentBase.StateHasChanged"/> before and after. Shows <paramref name="successMessage"/> on success or the exception message on failure.
    /// </summary>
    protected async Task RunAsync(Action<bool> setFlag, Func<Task> operation, string? successMessage = null)
    {
        setFlag(true);
        StateHasChanged();

        try
        {
            await operation();
            if (successMessage is not null)
            {
                ToastService.Show(successMessage, ToastType.Success);
            }
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, ToastType.Error);
        }
        finally
        {
            setFlag(false);
            StateHasChanged();
        }
    }
}

