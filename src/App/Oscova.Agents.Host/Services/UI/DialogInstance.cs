using System.Diagnostics.CodeAnalysis;

namespace Oscova.Agents.Host.Services.UI;

public sealed class DialogInstance
{
    internal IDialogService? _dialogService;
    public string DialogId = Guid.NewGuid().ToString();

    private readonly Dictionary<string, object?> _items = new();
    public IReadOnlyDictionary<string, object?> Items => _items;

    public void Set(string key, object? value) => _items[key] = value;

    public bool TryGet<T>(string key, out T? value)
    {
        if (_items.TryGetValue(key, out var obj) && obj is T t)
        {
            value = t;
            return true;
        }

        value = default;
        return false;
    }

    [return: NotNullIfNotNull(nameof(@default))]
    public T? Get<T>(string key, T? @default = default) => TryGet<T>(key, out var v) ? v : @default;

    public void Clear() => _items.Clear();

    public void Close(DialogResult dialogResult)
    {
        _dialogService?.Close(dialogResult);
    }

    public void Close<TData>(DialogResult dialogResult, TData? data)
    {
        _dialogService?.Close(dialogResult, data);
    }
}
