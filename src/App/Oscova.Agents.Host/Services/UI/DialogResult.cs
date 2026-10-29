namespace Oscova.Agents.Host.Services.UI;

public enum DialogResult
{
    Ok, Cancel
}

public class DialogResult<T>
{
    public DialogResult Result { get; init; }
    public T? Data { get; init; }

    public DialogResult(DialogResult result, T? data = default)
    {
        Result = result;
        Data = data;
    }

    public static implicit operator DialogResult(DialogResult<T> result) => result.Result;

    public bool IsOk => Result == DialogResult.Ok;
    public bool IsCancelled => Result == DialogResult.Cancel;
}
