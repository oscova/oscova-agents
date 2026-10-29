using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services.UI;
using Oscova.Agents.Infrastructure.Conversations;

namespace Oscova.Agents.Host.Components.Chat;

public partial class ChatPanel
{
    private ChatGoal? _goal;
    private ConversationGoalStore? _goalStore;
    private string? _goalThreadId;
    private bool? _goalEditing;
    private string _goalBorrowedText = string.Empty;
    private IReadOnlyList<PendingAttachment> _goalBorrowedAttachments = [];
    private IReadOnlyList<WorkspaceFileReference> _goalBorrowedFiles = [];
    private string? _goalControlMessage;
    private bool _goalSaving;

    private void OnGoalChanged(object? sender, EventArgs args)
    {
        if (!_isDisposed) { QueueProjectionRefresh(); }
    }

    private async Task RefreshGoalAsync()
    {
        var threadId = ChatThread.Id;
        try
        {
            var goal = await ChatApiClient.GetGoalAsync(threadId);
            if (!_isDisposed && ChatThread.Id == threadId) { _goal = goal; _goalThreadId = threadId; }
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Could not read goal state for thread {ThreadId}.", threadId); }
    }

    private async Task<bool> TryExecuteGoalCommandAsync()
    {
        if (_goalEditing is not null) { await SaveComposerGoalAsync(); return true; }

        var text = _inputText.Trim();
        if (!text.Equals("/goal", StringComparison.OrdinalIgnoreCase)
            && !(text.Length > 5 && text.StartsWith("/goal", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(text[5]))) { return false; }

        var thread = ChatThread;
        await RefreshGoalAsync();
        // A delayed command belongs only to the conversation where it was submitted.
        if (_isDisposed || !ReferenceEquals(thread, ChatThread)) { return true; }

        if (_goal is not null)
        {
            // Keep the command available to retry after the existing goal is cleared.
            _liveAnnouncement = Loc.GetOrDefault(ChatTextKeys.GoalClearFirst, "Clear the existing goal before starting another.");
            ToastService.Show(_liveAnnouncement, ToastType.Warning);
            _slashMenuDismissed = true;
            return true;
        }

        var objective = text.Length > 5 ? text[5..].Trim() : string.Empty;
        BeginGoalComposer(false, objective);
        if (objective.Length > 0) { await SaveComposerGoalAsync(); }

        return true;
    }

    private void BeginGoalComposer(bool editing, string objective)
    {
        _goalBorrowedText = editing || !_inputText.TrimStart().StartsWith("/goal", StringComparison.OrdinalIgnoreCase)
            ? _inputText : string.Empty;
        _goalBorrowedAttachments = _pendingAttachments.ToArray();
        _pendingAttachments.Clear();
        _goalBorrowedFiles = _pendingFiles.ToArray();
        _pendingFiles.Clear();
        _goalEditing = editing;
        _inputText = objective;
        _goalControlMessage = null;
        _slashMenuDismissed = true;
    }

    private void EditComposerGoal()
    {
        if (_goal is not null) { BeginGoalComposer(true, _goal.Objective); }
    }

    private Task CancelComposerGoalAsync()
    {
        RestoreGoalComposer();
        return Task.CompletedTask;
    }

    private void RestoreGoalComposer()
    {
        if (_goalEditing is null) { return; }

        _inputText = _goalBorrowedText;
        _pendingAttachments.Clear();
        _pendingAttachments.AddRange(_goalBorrowedAttachments);
        _goalBorrowedAttachments = [];
        _pendingFiles.Clear();
        _pendingFiles.AddRange(_goalBorrowedFiles);
        _goalBorrowedFiles = [];
        _goalBorrowedText = string.Empty;
        _goalEditing = null;
        _goalControlMessage = null;
    }

    private async Task SaveComposerGoalAsync()
    {
        if (_goalEditing is null || _goalSaving || string.IsNullOrWhiteSpace(_inputText)) { return; }

        var thread = ChatThread;
        var editing = _goalEditing.Value;
        var request = new SaveChatGoalRequest(_inputText.Trim(), ActiveAgentDefinition.Id, SelectedProvider?.Id.ToString());
        _goalSaving = true;
        try
        {
            if (!editing && thread.DisplayMessages.Count == 0 && _goal is null && !HasLiveRun)
            {
                await ChatApiClient.CreateThreadAsync(thread);
            }

            if (_isDisposed || !ReferenceEquals(thread, ChatThread)) { return; }

            var goal = await ChatApiClient.SaveGoalAsync(thread.Id, request, editing);
            if (!_isDisposed && ReferenceEquals(thread, ChatThread))
            {
                _goal = goal;
                _goalThreadId = thread.Id;
                RestoreGoalComposer();
                await ApplyLatestProjectionAsync(thread.Id, hydration: true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not save goal for thread {ThreadId}.", thread.Id);
            if (!_isDisposed && ReferenceEquals(thread, ChatThread))
            {
                _goalControlMessage = Loc.GetOrDefault(ChatTextKeys.GoalSaveFailed, "Couldn't save goal");
            }
        }
        finally { _goalSaving = false; }
    }

    private Task PauseComposerGoalAsync() => ChangeComposerGoalAsync(() => ChatApiClient.PauseGoalAsync(ChatThread.Id));
    private async Task ResumeComposerGoalAsync()
    {
        if (ChatThread.DisplayMessages.Any(message => message.Segments.Any(segment =>
            UserQuestions.TryRead(message, segment, out var card) && UserQuestions.GetState(ChatThread, card!) == UserQuestions.CardState.Pending)))
        {
            _goalControlMessage = Loc.GetOrDefault(ChatTextKeys.GoalAnswerOrDismiss, "Answer or dismiss the question first");
            return;
        }

        await ChangeComposerGoalAsync(() => ChatApiClient.ResumeGoalAsync(ChatThread.Id));
    }
    private Task ClearComposerGoalAsync() => ChangeComposerGoalAsync(() => ChatApiClient.ClearGoalAsync(ChatThread.Id));

    private async Task ChangeComposerGoalAsync(Func<Task> change)
    {
        if (_goalSaving) { return; }

        _goalSaving = true;
        _goalControlMessage = null;
        try { await change(); await RefreshGoalAsync(); }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not change goal for thread {ThreadId}.", ChatThread.Id);
            _goalControlMessage = Loc.GetOrDefault(ChatTextKeys.GoalActionFailed, "Couldn't update goal");
        }
        finally { _goalSaving = false; }
    }
}
