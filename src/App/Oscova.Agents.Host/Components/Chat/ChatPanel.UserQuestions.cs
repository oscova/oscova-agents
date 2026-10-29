using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Host.Server.Chat;

namespace Oscova.Agents.Host.Components.Chat;

public partial class ChatPanel
{
    private (string ThreadId, string MessageId, string CallId, string RunId)? _questionAttempt;
    private bool _questionNeedsRefresh;

    // Answers use ordinary run admission while leaving the composer's draft and attachments intact.
    private async Task SubmitUserQuestionsAsync(UserQuestionAnswer answer)
    {
        var threadId = ChatThread.Id;
        CancellationTokenSource? startCts = null;
        try
        {
            if (_questionNeedsRefresh)
            {
                await RefreshQuestionThreadAsync(threadId);
                _questionNeedsRefresh = false;
            }

            if (_isDisposed || ChatThread.Id != threadId) return;
            var card = UserQuestions.Find(ChatThread, answer.MessageId, answer.CallId);
            if (card is null || UserQuestions.GetState(ChatThread, card) != UserQuestions.CardState.Pending) return;
            if (_questionAttempt is not { } previous || previous.ThreadId != threadId ||
                previous.MessageId != card.MessageId || previous.CallId != card.CallId)
                _questionAttempt = (threadId, card.MessageId, card.CallId, $"run_{Guid.NewGuid():N}");
            var runId = _questionAttempt.Value.RunId;
            // While the run streams or workers run, the answer joins the live run instead of starting a local one.
            startCts = _runState == ChatRunState.AwaitingSubagents || IsRunActive ? new CancellationTokenSource() : BeginLocalRun(runId);
            var accepted = false;
            try
            {
                accepted = await ChatApiClient.TryStartRunAsync(threadId, new StartChatRunRequest
                {
                    RunId = runId,
                    UserMessageId = card.SavedState!.AnswerMessageId,
                    QuestionAnswer = answer,
                    AgentId = ChatThread.AgentId,
                    ModelProviderId = SelectedModelProviderId?.ToString(),
                    ApprovalRunMode = _toolApprovalRunMode
                }, startCts.Token, ConfirmBudgetAsync);
                if (accepted) await ApplyLatestProjectionAsync(threadId, hydration: true);
            }
            finally
            {
                if (!accepted)
                {
                    _questionNeedsRefresh = true;
                    try
                    {
                        await RefreshQuestionThreadAsync(threadId);
                        _questionNeedsRefresh = false;
                    }
                    finally
                    {
                        // A failed GET must not strand an optimistic local run with no server execution.
                        if (!_isDisposed && ChatThread.Id == threadId && _activeOperationId == runId)
                        {
                            var hasActiveRun = RunProjections.TryGetSnapshot(UserScope.UserId, threadId, out var snapshot) &&
                                snapshot is not null && snapshot.Phase != ChatRunProjectionPhase.Terminal;
                            if (!hasActiveRun) ResetActiveRun();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to submit question {CallId} in message {MessageId} on thread {ThreadId}.",
                answer.CallId, answer.MessageId, threadId);
            throw;
        }
        finally
        {
            if (ReferenceEquals(_runStartCts, startCts)) _runStartCts = null;
            startCts?.Dispose();
        }
    }

    private async Task DismissUserQuestionsAsync(UserQuestions.Card card)
    {
        var threadId = ChatThread.Id;
        try
        {
            await ChatApiClient.TryDismissQuestionAsync(threadId, card.MessageId, card.CallId);
            await RefreshQuestionThreadAsync(threadId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to dismiss question {CallId} in message {MessageId} on thread {ThreadId}.",
                card.CallId, card.MessageId, threadId);
            throw;
        }
    }

    private async Task RefreshQuestionThreadAsync(string threadId)
    {
        var refreshed = await ReadDisplayedThreadAsync(threadId);
        if (_isDisposed || ChatThread.Id != threadId || refreshed is null) return;
        MergePersistedMessages(refreshed);
        await ApplyLatestProjectionAsync(threadId, hydration: true);
        ClearClosedQuestionAttempt();
        await InvokeAsync(StateHasChanged);
    }

    private void ClearClosedQuestionAttempt()
    {
        if (_questionAttempt is { } attempt)
        {
            var card = UserQuestions.Find(ChatThread, attempt.MessageId, attempt.CallId);
            if (card is null || UserQuestions.GetState(ChatThread, card) != UserQuestions.CardState.Pending)
            {
                _questionAttempt = null;
                _questionNeedsRefresh = false;
            }
        }
    }
}
