using Microsoft.AspNetCore.Components;

namespace Oscova.Agents.Host.Services.UI;

public interface IDialogService
{
    Task ShowDialog(string dialogId);
    Task CloseDialog(string dialogId);
    Task<DialogResult> ShowAsync<T>() where T : IComponent;
    Task<DialogResult> ShowAsync<T>(IDictionary<string, object?>? parameters) where T : IComponent;
    Task<DialogResult> ShowAsync<T>(string title, string message) where T : IComponent;
    Task<DialogResult<TData>> ShowAsync<T, TData>() where T : IComponent;
    Task<DialogResult<TData>> ShowAsync<T, TData>(IDictionary<string, object?>? parameters) where T : IComponent;
    void Close(DialogResult dialogResult);
    void Close<TData>(DialogResult dialogResult, TData? data);

    event EventHandler<DialogEventArgs>? DialogShowing;
    event EventHandler<DialogEventArgs>? DialogClosing;
}
