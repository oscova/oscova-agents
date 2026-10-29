using Oscova.Agents.Plugins;
using Oscova.Agents.Host.Components.Dialogs;
using Oscova.Agents.Host.Components.Dialogs.Settings;
namespace Oscova.Agents.Host.Services.UI;
/// <summary>Adapts the existing editor, confirmation and toast services for compiled settings components.</summary>
public sealed class PluginSettingsUi(IDialogService dialogs, IToastService toasts) : IPluginSettingsUi
{
    /// <inheritdoc />
    public async Task<bool> EditTextAsync(string title, Func<Task<string>> load, Func<string, Task> save, string? helpTopic = null)
        => await dialogs.ShowAsync<FileEditorDialog>(new Dictionary<string, object?>
        { ["FileName"] = title, ["LoadContentAsync"] = load, ["SaveContentAsync"] = save, ["HelpTopic"] = helpTopic }) == DialogResult.Ok;
    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message)
        => await dialogs.ShowAsync<ConfirmDialog>(title, message) == DialogResult.Ok;
    /// <inheritdoc />
    public void Notify(string message, PluginNotificationKind kind) => toasts.Show(message, kind switch
    { PluginNotificationKind.Success => ToastType.Success, PluginNotificationKind.Warning => ToastType.Warning, PluginNotificationKind.Error => ToastType.Error, _ => ToastType.Info });
}
