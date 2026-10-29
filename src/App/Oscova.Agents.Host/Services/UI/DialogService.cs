using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Oscova.Agents.Host.Services.UI;

public sealed class DialogService : IDialogService, IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _moduleTask;
    private readonly Stack<object> _tcsStack = new();

    public DialogService(IJSRuntime jsRuntime)
    {
        _moduleTask = new(() => jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./js/ui-services.js").AsTask());
    }

    public async Task ShowDialog(string dialogId)
    {
        var module = await _moduleTask.Value.ConfigureAwait(false);
        await module.InvokeVoidAsync("showModal", dialogId).ConfigureAwait(false);
    }

    public async Task CloseDialog(string dialogId)
    {
        var module = await _moduleTask.Value.ConfigureAwait(false);
        await module.InvokeVoidAsync("closeModal", dialogId).ConfigureAwait(false);
    }

    public Task<DialogResult> ShowAsync<T>() where T : IComponent
        => ShowAsync<T>(parameters: null);

    public async Task<DialogResult> ShowAsync<T>(IDictionary<string, object?>? parameters) where T : IComponent
    {
        var tcs = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tcsStack.Push(tcs);

        DialogShowing?.Invoke(this, new DialogEventArgs
        {
            ComponentType = typeof(T),
            Parameters = parameters
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    public Task<DialogResult> ShowAsync<T>(string title, string message) where T : IComponent
    {
        var parameters = new Dictionary<string, object?>
        {
            { "Title", title },
            { "Message", message }
        };
        return ShowAsync<T>(parameters);
    }

    public Task<DialogResult<TData>> ShowAsync<T, TData>() where T : IComponent
        => ShowAsync<T, TData>(parameters: null);

    public async Task<DialogResult<TData>> ShowAsync<T, TData>(IDictionary<string, object?>? parameters) where T : IComponent
    {
        var tcs = new TaskCompletionSource<DialogResult<TData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tcsStack.Push(tcs);

        DialogShowing?.Invoke(this, new DialogEventArgs
        {
            ComponentType = typeof(T),
            Parameters = parameters
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    public void Close(DialogResult dialogResult)
    {
        if (_tcsStack.Count == 0)
        {
            DialogClosing?.Invoke(this, new DialogEventArgs());
            return;
        }

        var tcs = _tcsStack.Pop();
        CompleteAfterClose(() =>
        {
            if (tcs is TaskCompletionSource<DialogResult> simpleTcs)
            {
                simpleTcs.SetResult(dialogResult);
            }
            else
            {
                var tcsType = tcs.GetType();
                var resultType = tcsType.GetGenericArguments()[0];
                var dataType = resultType.GetGenericArguments()[0];
                var genericResultType = typeof(DialogResult<>).MakeGenericType(dataType);
                var result = Activator.CreateInstance(genericResultType, dialogResult, null);
                var setResultMethod = tcsType.GetMethod("SetResult");
                setResultMethod?.Invoke(tcs, [result]);
            }
        });
    }

    public void Close<TData>(DialogResult dialogResult, TData? data)
    {
        if (_tcsStack.Count == 0)
        {
            DialogClosing?.Invoke(this, new DialogEventArgs());
            return;
        }

        var tcs = _tcsStack.Pop();
        CompleteAfterClose(() =>
        {
            if (tcs is TaskCompletionSource<DialogResult<TData>> genericTcs)
            {
                genericTcs.SetResult(new DialogResult<TData>(dialogResult, data));
            }
            else if (tcs is TaskCompletionSource<DialogResult> simpleTcs)
            {
                simpleTcs.SetResult(dialogResult);
            }
        });
    }

    private void CompleteAfterClose(Action complete)
    {
        // Resume the caller only after the native modal stops making its opener inert.
        if (DialogClosing is { } closing)
        {
            closing(this, new DialogEventArgs { OnClosed = complete });
        }
        else
        {
            complete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_moduleTask.IsValueCreated)
        {
            try
            {
                var module = await _moduleTask.Value.ConfigureAwait(false);
                await module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // Circuit disposed; safely ignore.
            }
            catch (ObjectDisposedException)
            {
                // JS runtime/module disposed; safely ignore.
            }
        }
    }

    public event EventHandler<DialogEventArgs>? DialogShowing;
    public event EventHandler<DialogEventArgs>? DialogClosing;
}
