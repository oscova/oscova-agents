using Microsoft.AspNetCore.Components;
using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Components.Dialogs;

namespace Oscova.Agents.Host.Services.UI;

/// <summary>Connects an open Workspace tab to its existing preview and guarded close action.</summary>
public sealed record WorkspaceDocumentTab(string Key, string Name, string Path, string Icon,
    Func<Task> Select, Func<Task> Close);

/// <summary>Retains one open file's draft and original API identity within the current Workspace.</summary>
public sealed class WorkspaceFileDraft(string key, string name,
    Func<CancellationToken, Task<ProjectFilePreview>> load,
    Func<SaveProjectFileRequest, CancellationToken, Task<ProjectFileSaveResult>> save)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public ProjectFilePreview? Snapshot { get; set; }
    public long? RefreshVersion { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsDirty { get; set; }
    public bool IsEditing { get; set; }
    public bool ShowSource { get; set; }
    public bool IsBusy { get; set; }
    public string? ErrorKey { get; set; }
    public string? ConflictRevision { get; set; }
    public Func<CancellationToken, Task<ProjectFilePreview>> Load { get; } = load;
    public Func<SaveProjectFileRequest, CancellationToken, Task<ProjectFileSaveResult>> Save { get; } = save;

    /// <summary>Discards in-memory changes without writing or closing the file.</summary>
    public void Discard()
    {
        Text = Snapshot?.Text ?? string.Empty;
        IsDirty = false;
        IsEditing = false;
        ShowSource = false;
        ErrorKey = null;
        ConflictRevision = null;
    }
}

/// <summary>Keeps open drafts alive across conditional Workspace views without persisting them.</summary>
public sealed class WorkspaceEditingState(ILogger<WorkspaceEditingState> logger)
{
    public IList<WorkspaceDocumentTab> Documents { get; } = new List<WorkspaceDocumentTab>();
    public string? SelectedDocumentKey { get; set; }
    public IDictionary<string, WorkspaceFileDraft> Files { get; } = new Dictionary<string, WorkspaceFileDraft>(StringComparer.Ordinal);
    public IDictionary<WorkspaceFileDraft, Func<Task>> Captures { get; } = new Dictionary<WorkspaceFileDraft, Func<Task>>();
    public EventCallback Changed { get; set; }
    public bool IsDirty => Files.Values.Any(file => file.IsDirty);
    public bool IsBusy => Files.Values.Any(file => file.IsBusy);
    public long SaveVersion { get; private set; }

    /// <summary>Captures Prism before the selected editor is replaced.</summary>
    public async Task CaptureAsync()
    {
        foreach (var capture in Captures.Values.ToArray()) { await capture(); }
    }

    /// <summary>Resolves dirty files before an action replaces or removes their original scope.</summary>
    public async Task<bool> ConfirmAsync(IDialogService dialogs, IReadOnlyList<WorkspaceFileDraft>? files = null)
    {
        if (IsBusy) { return false; }

        await CaptureAsync();
        var dirty = (files ?? Files.Values.ToArray()).Where(file => file.IsDirty).ToArray();
        if (dirty.Length == 0) { return true; }

        var result = await dialogs.ShowAsync<WorkspaceFileDecisionDialog>(new Dictionary<string, object?>
        { ["State"] = this, ["Files"] = dirty });
        await Changed.InvokeAsync();
        return result == DialogResult.Ok;
    }

    /// <summary>Loads a complete baseline only after a successful response and retains a failed draft.</summary>
    public async Task<bool> ReloadAsync(WorkspaceFileDraft file)
    {
        if (file.IsBusy) { return false; }

        file.IsBusy = true;
        var preserveView = !file.IsDirty;
        var editing = file.IsEditing;
        var source = file.ShowSource;
        try
        {
            var snapshot = await file.Load(CancellationToken.None);
            // Typing during an automatic refresh keeps the draft and the baseline its save is checked against.
            if (preserveView && file.IsDirty) { return false; }

            if (snapshot.State != ProjectReadState.Ready && file.IsDirty)
            { file.ErrorKey = ChatTextKeys.WorkspaceLoadFailed; return false; }

            file.Snapshot = snapshot;
            file.Discard();
            if (preserveView) { file.IsEditing = editing; file.ShowSource = source; }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Workspace snapshot could not be loaded for {FileIdentity}.", file.Key);
            file.ErrorKey = ChatTextKeys.WorkspaceLoadFailed;
            return false;
        }
        finally { file.IsBusy = false; await Changed.InvokeAsync(); }
    }

    /// <summary>Saves an explicitly captured draft and changes its baseline only on confirmed success.</summary>
    public async Task<bool> SaveAsync(WorkspaceFileDraft file, bool overwrite = false)
    {
        // A clean preview reload must not interrupt the next write in an already confirmed save sequence.
        if (file.IsBusy || !file.IsDirty || file.Snapshot?.CanEdit != true) { return !file.IsDirty; }

        file.IsBusy = true;
        file.ErrorKey = null;
        await Changed.InvokeAsync();
        try
        {
            var expected = overwrite ? file.ConflictRevision : file.Snapshot.Revision;
            var result = await file.Save(new(file.Text, expected!), CancellationToken.None);
            if (result.Conflict)
            {
                file.ConflictRevision = result.Revision;
                file.ErrorKey = ChatTextKeys.WorkspaceConflictMessage;
                return false;
            }

            file.Snapshot = file.Snapshot with { Text = file.Text, Revision = result.Revision };
            file.IsDirty = false;
            file.ConflictRevision = null;
            SaveVersion++;
            return true;
        }
        catch (Exception ex)
        {
            // A lost node reply can follow a completed write, keep the old revision for explicit recovery.
            logger.LogWarning(ex, "Workspace save was not confirmed for {FileIdentity}.", file.Key);
            file.ErrorKey = ChatTextKeys.WorkspaceSaveFailed;
            return false;
        }
        finally { file.IsBusy = false; await Changed.InvokeAsync(); }
    }
}
