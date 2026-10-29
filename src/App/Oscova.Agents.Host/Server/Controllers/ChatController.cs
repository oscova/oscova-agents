using Oscova.Agents.Definitions;
using Oscova.Agents.Conversations;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Host.Server.Chat;
using Oscova.Agents.Host.Server.Agents;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Host.Server.Services;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.ApiContracts.Projects;
using Oscova.Agents.Workspace;
using Oscova.Agents.Policies;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Hooks;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Infrastructure.Metrics;
using Oscova.Agents.Host.Services;
using static Oscova.Agents.Host.Server.Chat.ChatRunHost;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Oscova.Agents.Host.Server.Security.RequireUnlockedVault]
[Authorize]
[Route("api/chat")]
[IgnoreAntiforgeryToken]
public sealed class ChatController(
    ChatSessionService chatSessionService,
    ChatContextPreflightService chatContextPreflightService,
    ThreadRunGuard threadRunGuard,
    ChatRunHost chatRunHost,
    ChatRunProjectionStore projectionStore,
    IChatAttachmentService chatAttachmentService,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
    UserServiceManager userServiceManager,
    ModelBudgetService budgets,
    ILogger<ChatController> logger,
    ChatSteeringRegistry steeringRegistry,
    WorkspaceFileEditingService editing,
    ArtifactFileAccessService files)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpGet("threads")]
    public async Task<ActionResult<IReadOnlyList<ChatThread>>> GetThreads(CancellationToken ct)
    {
        var threads = await chatSessionService.GetThreadsAsync(GetUserId(), ct);
        return Ok(threads);
    }

    /// <summary>Returns relevance-ranked conversation ids for the signed-in user.</summary>
    [HttpGet("threads/search")]
    public async Task<ActionResult<IReadOnlyList<string>>> SearchThreads(
        [FromQuery] string? query,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(Array.Empty<string>());
        }

        return Ok(await chatSessionService.SearchThreadIdsAsync(GetUserId(), query, ct));
    }

    [HttpGet("threads/ephemeral")]
    public async Task<ActionResult<IReadOnlyList<ChatThread>>> GetEphemeralThreads(CancellationToken ct)
    {
        var threads = await chatSessionService.GetIncognitoThreadsAsync(GetUserId(), ct);
        return Ok(threads);
    }

    [HttpGet("threads/recent-current")]
    public async Task<ActionResult<ChatThread>> GetRecentCurrentThread(CancellationToken ct)
    {
        try
        {
            var thread = await chatSessionService.GetRecentCurrentThreadAsync(GetUserId(), ct);
            return thread is null ? NoContent() : Ok(thread);
        }
        catch (InvalidDataException ex) { return ChatDataUnavailable(ex); }
    }

    [HttpGet("threads/{threadId}")]
    public async Task<ActionResult<ChatThread>> GetThread(string threadId, CancellationToken ct)
    {
        try
        {
            var thread = await chatSessionService.GetThreadAsync(GetUserId(), threadId, ct);
            return thread is null ? NotFound() : Ok(thread);
        }
        catch (InvalidDataException ex) { return ChatDataUnavailable(ex, threadId); }
    }

    /// <summary>Returns display exchanges without sending the serialized model session to the chat panel.</summary>
    [HttpGet("threads/{threadId}/view")]
    public async Task<ActionResult<ChatThreadViewResponse>> GetThreadView(string threadId, CancellationToken ct,
        [FromQuery] string? beforeMessageId = null, [FromQuery] string? firstMessageId = null)
    {
        try
        {
            var view = await chatSessionService.GetThreadViewAsync(GetUserId(), threadId, ct, beforeMessageId, firstMessageId);
            return view is null ? NotFound() : Ok(view);
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResult { Message = ex.Message }); }
        catch (InvalidDataException ex) { return ChatDataUnavailable(ex, threadId); }
    }

    /// <summary>Resumes the recent conversation using its bounded display view.</summary>
    [HttpGet("threads/recent-current/view")]
    public async Task<ActionResult<ChatThreadViewResponse>> GetRecentCurrentThreadView(CancellationToken ct)
    {
        try
        {
            var view = await chatSessionService.GetRecentCurrentThreadViewAsync(GetUserId(), ct);
            return view is null ? NoContent() : Ok(view);
        }
        catch (InvalidDataException ex) { return ChatDataUnavailable(ex); }
    }

    /// <summary>Reports incompatible saved chat data without exposing exception details.</summary>
    private ObjectResult ChatDataUnavailable(InvalidDataException exception, string? threadId = null)
    {
        logger.LogWarning(exception, "Saved chat data is unavailable for user {UserId}, thread {ThreadId}.", GetUserId(), threadId);
        return UnprocessableEntity(new ErrorResult { Message = exception.Message });
    }

    [HttpGet("threads/{threadId}/session-messages")]
    public async Task<ActionResult<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>> GetSessionMessages(string threadId, CancellationToken ct)
    {
        var messages = await chatSessionService.GetSessionMessagesAsync(GetUserId(), threadId, ct);
        return Ok(messages);
    }

    [HttpGet("threads/{threadId}/session-inspector")]
    public async Task<ActionResult<ChatSessionInspectorSnapshot>> GetSessionInspector(string threadId, CancellationToken ct)
    {
        var snapshot = await chatSessionService.GetSessionInspectorAsync(GetUserId(), threadId, ct);
        return Ok(snapshot);
    }

    /// <summary>Compacts the model context for one conversation owned by the signed-in user.</summary>
    [HttpPost("threads/{threadId}/compact")]
    public async Task<ActionResult<bool>> CompactThread(
        string threadId,
        [FromQuery] Guid? modelProviderId,
        [FromQuery] bool continueOverBudget,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return BadRequest();
        }

        var userId = GetUserId();
        var scope = await GetUserScopeAsync(ct);
        // Thread lookup validates the ID before model and budget admission.
        try
        {
            var thread = await chatSessionService.GetThreadAsync(userId, threadId, ct);
            if (thread is null) { return NotFound(); }

            using var lease = threadRunGuard.Acquire(userId, threadId, $"compact_{Guid.NewGuid():N}");
            if (lease is null)
            {
                return Conflict();
            }
            // Empty conversations need no model, but still honor an active run's ownership.
            if (thread.SessionElement is null) { return Ok(false); }

            var definition = scope.AppService.ResolveAgentDefinition(thread.AgentId);
            var selected = scope.AppService.ResolveChatModelProvider(definition, modelProviderId?.ToString());
            var resolution = await scope.AppService.GetChatClientResolutionAsync(definition, selected);
            var budget = await budgets.GetAsync(userId, resolution.EffectiveProvider.Id, ct);
            if (budget.IsExceeded && !(continueOverBudget && budget.CanOverride))
            {
                return StatusCode(StatusCodes.Status402PaymentRequired, budget.ForViewer());
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
            var result = await chatSessionService.CompactThreadAsync(
                userId,
                threadId,
                modelProviderId?.ToString(),
                linkedCts.Token);

            return result switch
            {
                CompactThreadResult.NotFound => NotFound(),
                CompactThreadResult.Compacted => Ok(true),
                _ => Ok(false)
            };
        }
        catch (ArgumentException ex) when (string.Equals(ex.ParamName, nameof(threadId), StringComparison.Ordinal))
        {
            return BadRequest();
        }
    }

    [HttpGet("threads/{threadId}/todos")]
    public async Task<ActionResult<IReadOnlyList<ChatTodoItem>>> GetTodos(
        string threadId,
        [FromQuery] string? modelProviderId,
        CancellationToken ct)
    {
        var todos = await chatSessionService.GetTodosAsync(GetUserId(), threadId, modelProviderId, ct);
        return Ok(todos);
    }

    [HttpGet("threads/{threadId}/agent-conversations")]
    public async Task<ActionResult<IReadOnlyList<AgentConversationView>>> GetAgentConversations(
        string threadId,
        [FromQuery] string? modelProviderId,
        CancellationToken ct)
    {
        var conversations = await chatSessionService.GetAgentConversationsAsync(GetUserId(), threadId, modelProviderId, ct);
        return Ok(conversations);
    }

    /// <summary>Admits a web chat run without attaching the request to its execution lifetime.</summary>
    [HttpPost("threads/{threadId}/runs")]
    public async Task<IActionResult> StartRun(
        string threadId,
        [FromBody] StartChatRunRequest? request,
        CancellationToken requestAborted)
    {
        if (string.IsNullOrWhiteSpace(threadId) ||
            string.IsNullOrWhiteSpace(request?.RunId) ||
            string.IsNullOrWhiteSpace(request.UserMessageId) ||
            request.AttachmentIds is null || !ChatAttachmentLimits.AreWorkspaceFilesValid(request.WorkspaceFiles) ||
            (request.RegenerateAssistantMessageId is not null &&
             string.IsNullOrWhiteSpace(request.RegenerateAssistantMessageId)) ||
            !Enum.IsDefined(request.ApprovalRunMode))
        {
            return BadRequest();
        }

        var userId = GetUserId();
        ThreadRunGuard.RunLease? admissionLease = null;
        try
        {
            if (request.ContinueSession &&
                (request.QuestionAnswer is not null || request.RegenerateAssistantMessageId is not null ||
                 request.AttachmentIds.Count != 0 || request.WorkspaceFiles.Count != 0 || !string.IsNullOrWhiteSpace(request.UserText)))
            {
                return BadRequest();
            }

            if (request.QuestionAnswer is not null || request.ContinueSession)
            {
                if (request.AttachmentIds.Count != 0 || request.WorkspaceFiles.Count != 0 || request.RegenerateAssistantMessageId is not null)
                {
                    return BadRequest();
                }

                admissionLease = threadRunGuard.Acquire(userId, threadId, request.RunId);
                // A busy thread can still take an answer through its live run below.
                if (admissionLease is null && request.QuestionAnswer is null) { return Conflict(); }
            }

            var attachmentIds = request.AttachmentIds;
            var workspaceFiles = request.WorkspaceFiles;
            var userText = request.UserText ?? string.Empty;
            var regenerateAssistantMessageId = request.RegenerateAssistantMessageId;
            if (!request.ContinueSession && request.QuestionAnswer is null && regenerateAssistantMessageId is null &&
                (attachmentIds.Count > ChatAttachmentLimits.MaxFilesPerMessage ||
                 attachmentIds.Any(string.IsNullOrWhiteSpace) ||
                 attachmentIds.Distinct(StringComparer.Ordinal).Count() != attachmentIds.Count ||
                 string.IsNullOrWhiteSpace(userText) && attachmentIds.Count == 0 && workspaceFiles.Count == 0))
            {
                return BadRequest();
            }

            ChatThread? thread;
            try
            {
                thread = await chatSessionService.GetThreadAsync(userId, threadId, requestAborted);
            }
            catch (InvalidDataException ex) { return ChatDataUnavailable(ex, threadId); }
            catch (ArgumentException)
            {
                return BadRequest();
            }

            if (thread is null)
            {
                return NotFound();
            }

            if (request.ContinueSession)
            {
                if (ChatThreadAgentSessionStore.GetRunOutcome(thread.SessionElement) != ChatRunOutcome.Stopped ||
                    !thread.DisplayMessages.Any(message => message.From == MessageOwner.User && !message.IsInteractionResponse))
                {
                    return Conflict();
                }
            }

            if (request.QuestionAnswer is { } answer)
            {
                var card = UserQuestions.Find(thread, answer.MessageId, answer.CallId);
                if (card is null) { return NotFound(); }

                if (UserQuestions.GetState(thread, card) != UserQuestions.CardState.Pending) { return Conflict(); }

                if (request.UserMessageId != card.SavedState!.AnswerMessageId ||
                    request.AgentId is not null && !string.Equals(request.AgentId, thread.AgentId, StringComparison.OrdinalIgnoreCase) ||
                    !UserQuestions.TryFormatAnswers(card, answer.Answers, out userText)) { return BadRequest(); }

                if (admissionLease is null)
                {
                    // An answer while workers run goes to the owning run, never to a second or revived run.
                    try
                    {
                        var steered = await steeringRegistry.TrySteerAsync(userId, threadId, new ChatDisplayMessage
                        {
                            MessageId = request.UserMessageId,
                            Channel = HookExecutionKinds.WebChat,
                            From = MessageOwner.User,
                            Text = userText,
                            RecipientAgentId = thread.DisplayMessages.First(message => message.MessageId == card.MessageId).AgentId
                        }, HookExecutionKinds.WebChat, channelId: null, requestAborted);
                        return steered is null ? Conflict() : Accepted();
                    }
                    catch (HookDeniedException ex)
                    {
                        return Problem(statusCode: StatusCodes.Status403Forbidden, detail: ex.Message);
                    }
                }
            }
            else if (regenerateAssistantMessageId is null && UserQuestions.IsReservedAnswerMessageId(thread, request.UserMessageId))
            {
                return BadRequest();
            }

            if (regenerateAssistantMessageId is not null)
            {
                var targetIndex = thread.DisplayMessages.Count - 1;
                if (targetIndex < 1 ||
                    thread.DisplayMessages[targetIndex].From != MessageOwner.Bot ||
                    thread.DisplayMessages[targetIndex].IsInteractionResponse ||
                    !string.Equals(
                        thread.DisplayMessages[targetIndex].MessageId,
                        regenerateAssistantMessageId,
                        StringComparison.Ordinal))
                {
                    return Conflict();
                }

                ChatDisplayMessage? persistedUserMessage = null;
                for (var index = targetIndex - 1; index >= 0; index--)
                {
                    var candidate = thread.DisplayMessages[index];
                    if (candidate.From == MessageOwner.User && !candidate.IsInteractionResponse)
                    {
                        persistedUserMessage = candidate;
                        break;
                    }
                }

                if (persistedUserMessage is null ||
                    !string.Equals(
                        persistedUserMessage.MessageId,
                        request.UserMessageId,
                        StringComparison.Ordinal))
                {
                    return Conflict();
                }

                userText = persistedUserMessage.Text;
                workspaceFiles = persistedUserMessage.WorkspaceFiles;
                attachmentIds = persistedUserMessage.Attachments
                    .Select(attachment => attachment.Id)
                    .ToArray();
                if (attachmentIds.Count > ChatAttachmentLimits.MaxFilesPerMessage ||
                    attachmentIds.Any(string.IsNullOrWhiteSpace) ||
                    attachmentIds.Distinct(StringComparer.Ordinal).Count() != attachmentIds.Count ||
                    string.IsNullOrWhiteSpace(userText) && attachmentIds.Count == 0 && workspaceFiles.Count == 0)
                {
                    return BadRequest();
                }
            }
            else if (thread.DisplayMessages.Any(message =>
                         string.Equals(message.MessageId, request.UserMessageId, StringComparison.Ordinal)))
            {
                return Conflict();
            }

            if (attachmentIds.Any(id => chatAttachmentService.Get(userId, threadId, id, thread.IsIncognito) is null))
            {
                return BadRequest();
            }

            var userScope = await GetUserScopeAsync(requestAborted);
            var definition = userScope.AppService.ResolveAgentDefinition(AgentSelectionResolver.ResolveAgentId(
                request.AgentId, thread.AgentId, thread.DisplayMessages.Count > 0));
            var recipientId = request.ContinueSession ? ChatParticipantSessions.Read(thread.SessionElement).LastRecipientId : request.RecipientAgentId;
            if (request.QuestionAnswer is { } questionAnswer)
            {
                // Answers return to the agent that posted the card, including goal-only turns.
                recipientId = thread.DisplayMessages.First(message => message.MessageId == questionAnswer.MessageId).AgentId;
            }
            else if (regenerateAssistantMessageId is not null)
            {
                recipientId = thread.DisplayMessages.LastOrDefault(message => message.From == MessageOwner.User && !message.IsInteractionResponse)?.RecipientAgentId;
            }

            var owner = definition;
            var ownerRequiresLocalOnly = definition.IsLocalOnly;
            definition = await AgentSelectionResolver.ResolveRecipientAsync(userScope, thread, definition, recipientId, requestAborted);
            var providerId = AgentSelectionResolver.ResolveModelProviderId(owner, definition, request.ModelProviderId,
                userScope.ModelProviderSettings.SelectedChatModelProviderId?.ToString());
            var selected = userScope.AppService.ResolveChatModelProvider(definition, providerId);
            var resolution = await userScope.AppService.GetChatClientResolutionAsync(definition, selected, ownerRequiresLocalOnly);
            var budget = await budgets.GetAsync(userId, resolution.EffectiveProvider.Id, requestAborted);
            if (budget.IsExceeded && !(request.ContinueOverBudget && budget.CanOverride))
            {
                return StatusCode(StatusCodes.Status402PaymentRequired, budget.ForViewer());
            }

            var context = CreateRunContext(threadId, request.RunId, request.UserMessageId, userText,
                request.AgentId, request.ModelProviderId, request.ApprovalRunMode, attachmentIds, regenerateAssistantMessageId, request.ContinueSession, recipientId, workspaceFiles);
            if (admissionLease is not null)
            {
                if (!chatRunHost.TryStartDetached(userId, context, requestAborted, admissionLease, request.QuestionAnswer))
                {
                    return Conflict();
                }
                // Execution retains the lease that protected saved-state validation.
                admissionLease = null;
                return Accepted();
            }

            return chatRunHost.TryStartDetached(userId, context, requestAborted)
                ? Accepted()
                : Conflict();
        }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }
        finally
        {
            admissionLease?.Dispose();
        }
    }

    private ChatRequestContext CreateRunContext(
        string threadId,
        string runId,
        string userMessageId,
        string userText,
        string? agentId,
        string? modelProviderId,
        ToolApprovalRunMode approvalRunMode,
        IReadOnlyList<string> attachmentIds,
        string? regenerateAssistantMessageId,
        bool continueSession = false,
        string? recipientAgentId = null,
        IReadOnlyList<WorkspaceFileReference>? workspaceFiles = null)
    {
        var input = new RunAgentInput
        {
            ThreadId = threadId,
            RunId = runId,
            Messages =
            [
                new AGUIUserMessage
                {
                    Id = userMessageId,
                    Content = userText
                }
            ],
            ForwardedProperties = JsonSerializer.SerializeToElement(new
            {
                modelProviderId,
                agentId,
                recipientAgentId,
                approvalRunMode = approvalRunMode.ToString(),
                attachmentIds,
                workspaceFiles = workspaceFiles ?? [],
                regenerateAssistantMessageId,
                continueSession
            }, jsonOptions.Value.SerializerOptions)
        };
        return input.ToChatRequestContext(jsonOptions.Value.SerializerOptions);
    }

    /// <summary>Lists the follow-ups waiting to run after the live turn.</summary>
    [HttpGet("threads/{threadId}/follow-ups")]
    public IActionResult GetFollowUps(string threadId) =>
        Ok(chatRunHost.GetFollowUps(GetUserId(), threadId)
            .Select(item => new ChatFollowUpView { MessageId = item.MessageId, Text = item.Text, RecipientAgentId = item.RecipientAgentId, WorkspaceFiles = item.WorkspaceFiles })
            .ToList());

    /// <summary>Steers the live run, queues behind it, or starts a turn when the thread is idle.</summary>
    [HttpPost("threads/{threadId}/follow-ups")]
    public async Task<IActionResult> SendFollowUp(
        string threadId,
        [FromBody] SendChatFollowUpRequest? request,
        CancellationToken requestAborted)
    {
        if (string.IsNullOrWhiteSpace(threadId) ||
            string.IsNullOrWhiteSpace(request?.MessageId) ||
            !ChatAttachmentLimits.AreWorkspaceFilesValid(request.WorkspaceFiles) ||
            (string.IsNullOrWhiteSpace(request.Text) && request.WorkspaceFiles.Count == 0) ||
            !Enum.IsDefined(request.Mode) ||
            !Enum.IsDefined(request.ApprovalRunMode))
        {
            return BadRequest();
        }

        var userId = GetUserId();
        ChatThread? thread;
        try
        {
            thread = await chatSessionService.GetThreadAsync(userId, threadId, requestAborted);
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }

        if (thread is null)
        {
            return NotFound();
        }

        if (UserQuestions.IsReservedAnswerMessageId(thread, request.MessageId))
        {
            return BadRequest();
        }

        if (thread.DisplayMessages.Any(message =>
                string.Equals(message.MessageId, request.MessageId, StringComparison.Ordinal)))
        {
            return Conflict();
        }

        var context = CreateRunContext(
            threadId,
            $"run_{Guid.NewGuid():N}",
            request.MessageId,
            request.Text,
            request.AgentId,
            request.ModelProviderId,
            request.ApprovalRunMode,
            [],
            regenerateAssistantMessageId: null,
            recipientAgentId: request.RecipientAgentId, workspaceFiles: request.WorkspaceFiles);
        return await DispatchFollowUpAsync(
            userId,
            new QueuedFollowUp(request.MessageId, request.Text, context),
            steer: request.Mode == ChatFollowUpMode.Steer,
            atFront: false,
            requestAborted);
    }

    /// <summary>Moves a queued follow-up into the live run, or runs it next when the run cannot take it.</summary>
    [HttpPost("threads/{threadId}/follow-ups/{messageId}/steer")]
    public async Task<IActionResult> SteerFollowUp(string threadId, string messageId, CancellationToken requestAborted)
    {
        var userId = GetUserId();
        var taken = chatRunHost.TryTakeFollowUp(userId, threadId, messageId);
        if (taken is null)
        {
            return NotFound();
        }

        var accepted = false;
        try
        {
            var result = await DispatchFollowUpAsync(userId, taken.Value.Item, steer: true, atFront: true, requestAborted);
            accepted = result is OkObjectResult;
            return result;
        }
        finally
        {
            if (!accepted)
            {
                chatRunHost.RestoreFollowUp(userId, taken.Value.Item, taken.Value.Index);
            }
        }
    }

    /// <summary>Removes a queued follow-up, returning not found when it already left the queue.</summary>
    [HttpDelete("threads/{threadId}/follow-ups/{messageId}")]
    public IActionResult DeleteFollowUp(string threadId, string messageId)
    {
        return chatRunHost.TryRemoveFollowUp(GetUserId(), threadId, messageId) is null
            ? NotFound()
            : NoContent();
    }

    // Steer first when asked, otherwise queue behind the live run, or start at once when the thread is idle.
    private async Task<IActionResult> DispatchFollowUpAsync(
        string userId,
        QueuedFollowUp item,
        bool steer,
        bool atFront,
        CancellationToken requestAborted)
    {
        var scope = await GetUserScopeAsync(requestAborted);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, item.Context.Input.ThreadId, requestAborted);
        if (thread is null) { return NotFound(); }

        var owner = scope.AppService.ResolveAgentDefinition(thread.AgentId);
        AgentDefinition recipient;
        try { recipient = await AgentSelectionResolver.ResolveRecipientAsync(scope, thread, owner, item.RecipientAgentId, requestAborted); }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }

        if (steer)
        {
            var message = new ChatDisplayMessage
            {
                MessageId = item.MessageId,
                Channel = HookExecutionKinds.WebChat,
                From = MessageOwner.User,
                Text = item.Text,
                RecipientAgentId = recipient.Id,
                WorkspaceFiles = item.WorkspaceFiles
            };
            try
            {
                var runId = await steeringRegistry.TrySteerAsync(
                        userId,
                        item.Context.Input.ThreadId,
                        message,
                        HookExecutionKinds.WebChat,
                        channelId: null,
                        requestAborted);
                if (runId is not null)
                {
                    return Ok(new ChatFollowUpResult(ChatFollowUpDisposition.Steered, runId));
                }
            }
            catch (HookDeniedException ex)
            {
                return Problem(statusCode: StatusCodes.Status403Forbidden, detail: ex.Message);
            }
        }

        var started = chatRunHost.EnqueueOrStart(userId, item, atFront, requestAborted);
        if (started is null) { return Conflict("The conversation is busy. Try again when the current operation finishes."); }

        return Ok(new ChatFollowUpResult(
            started.Value ? ChatFollowUpDisposition.Started : ChatFollowUpDisposition.Queued,
            started.Value ? item.Context.Input.RunId : null));
    }

    /// <summary>Closes a saved question without starting a conversation turn.</summary>
    [HttpPost("threads/{threadId}/questions/{messageId}/{callId}/dismiss")]
    public async Task<IActionResult> DismissQuestion(string threadId, string messageId, string callId, CancellationToken ct)
    {
        var userId = GetUserId();
        using var lease = threadRunGuard.Acquire(userId, threadId, $"dismiss_{Guid.NewGuid():N}");
        // A card can close while its run waits for workers, without cancelling them.
        var liveRun = lease is null ? steeringRegistry.Find(userId, threadId) : null;
        if (lease is null && liveRun is null) { return Conflict(); }

        var scope = await GetUserScopeAsync(ct);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct);
        if (thread is null) return NotFound();
        var card = UserQuestions.Find(thread, messageId, callId);
        if (card is null) return NotFound();
        var state = UserQuestions.GetState(thread, card);
        if (state == UserQuestions.CardState.Dismissed) return NoContent();
        if (state != UserQuestions.CardState.Pending) return Conflict();
        var message = thread.DisplayMessages.First(message => message.MessageId == messageId);
        var copy = UserQuestions.WithState(message, callId, card.SavedState! with { Dismissed = true });
        if (liveRun is not null)
        {
            // The live run either took the answer already or will refuse it from now on.
            if (!await liveRun.TryReplaceAsync(copy, card.SavedState!.AnswerMessageId, ct)) { return Conflict(); }
        }
        else if (!await scope.ChatThreadRepository.TryReplaceDisplayMessageAsync(userId, threadId, copy, ct)) { return NotFound(); }

        await scope.ConversationGoals.UpdateAsync(userId, thread, goal =>
            goal?.Status == ChatGoalStatus.Blocked && goal.Reason == "Waiting for your answer"
                ? goal with { Reason = "Question dismissed" } : goal, ct);
        if (liveRun is null) { projectionStore.Remove(userId, threadId); }

        return NoContent();
    }

    [HttpPost("threads/{threadId}/context-preflight")]
    public async Task<ActionResult<ChatContextPreflightResponse>> GetContextPreflight(
        string threadId,
        [FromBody] ChatContextPreflightRequest? request,
        CancellationToken ct)
    {
        try
        {
            var response = await chatContextPreflightService.EstimateAsync(
                GetUserId(), threadId, request ?? new ChatContextPreflightRequest(), ct);
            return Ok(response);
        }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }
    }

    [HttpPost("threads/{threadId}/stop")]
    public async Task<IActionResult> StopRun(
        string threadId,
        [FromBody] StopChatRunRequest? request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.RunId))
        {
            return BadRequest();
        }

        var userId = GetUserId();
        var goalScope = await GetUserScopeAsync(ct);
        var goalThread = await goalScope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct);
        if (goalThread is not null)
        {
            await goalScope.ConversationGoals.UpdateAsync(userId, goalThread,
                goal => goal?.Status == ChatGoalStatus.Active && (goal.RunId == request.RunId
                    || goal.RunId is null && threadRunGuard.GetActiveRunId(userId, threadId) == request.RunId)
                    ? goal.WithStatus(ChatGoalStatus.Paused, "Stopped") : goal, ct);
        }

        if (threadRunGuard.CancelExact(userId, threadId, request.RunId))
        {
            return NoContent();
        }

        // The run may not have persisted its thread yet, so only defer after checking ownership.
        var thread = await chatSessionService.GetThreadAsync(userId, threadId, ct);
        if (thread is null)
        {
            return NotFound();
        }

        threadRunGuard.CancelOrDefer(userId, threadId, request.RunId);
        return NoContent();
    }

    /// <summary>Reads goal control state independently of transcript history.</summary>
    [HttpGet("threads/{threadId}/goal")]
    public async Task<ActionResult<ChatGoal>> GetGoal(string threadId, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(scope.UserId, threadId, ct);
        if (thread is null) { return NotFound(); }

        var goal = await scope.ConversationGoals.GetAsync(scope.UserId, thread, ct);
        return goal is null ? NoContent() : Ok(goal);
    }

    /// <summary>Enables continuation without submitting an ordinary user turn.</summary>
    [HttpPost("threads/{threadId}/goal")]
    public async Task<ActionResult<ChatGoal>> StartGoal(string threadId, SaveChatGoalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Objective)) { return BadRequest("Objective must not be blank."); }

        var scope = await GetUserScopeAsync(ct);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(scope.UserId, threadId, ct);
        if (thread is null) { return NotFound(); }

        var definition = scope.AppService.ResolveAgentDefinition(AgentSelectionResolver.ResolveAgentId(
            request.AgentId, thread.AgentId, thread.DisplayMessages.Count > 0));
        _ = scope.AppService.ResolveChatModelProvider(definition, request.ModelProviderId);
        await scope.AgentGoals.LoadAsync();
        ChatGoal? created = null;
        var saved = await scope.ConversationGoals.UpdateAsync(scope.UserId, thread, existing =>
        {
            if (existing is not null) { return existing; }

            created = new ChatGoal
            {
                Objective = request.Objective.Trim(), AgentId = definition.Id,
                ModelProviderId = request.ModelProviderId,
                MaxIterations = scope.AgentGoals.GetLimit(definition.Id) ?? int.MaxValue
            };
            return created;
        }, ct);
        if (created is null) { return Conflict(saved); }
        // Deletion may finish while the goal is saved, but must not leave an orphaned objective.
        if (!await scope.ChatThreadRepository.ThreadExistsAsync(scope.UserId, thread.Id, CancellationToken.None))
        {
            await scope.ConversationGoals.DeleteAsync(scope.UserId, thread, CancellationToken.None);
            return NotFound();
        }

        await chatRunHost.TryStartGoalAsync(scope.UserId, threadId, ct);
        return Ok(await scope.ConversationGoals.GetAsync(scope.UserId, thread, ct));
    }

    /// <summary>Updates the objective while preserving state, elapsed time and cycle allowance.</summary>
    [HttpPut("threads/{threadId}/goal")]
    public async Task<ActionResult<ChatGoal>> EditGoal(string threadId, SaveChatGoalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Objective)) { return BadRequest("Objective must not be blank."); }

        var scope = await GetUserScopeAsync(ct);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(scope.UserId, threadId, ct);
        if (thread is null) { return NotFound(); }

        var existing = await scope.ConversationGoals.GetAsync(scope.UserId, thread, ct);
        if (existing is null) { return NotFound(); }

        if (existing.Status == ChatGoalStatus.Achieved) { return Conflict("Clear the achieved goal before starting another."); }

        return Ok(await scope.ConversationGoals.UpdateAsync(scope.UserId, thread, goal =>
            goal?.Id == existing.Id && goal.Status != ChatGoalStatus.Achieved
                ? goal with { Objective = request.Objective.Trim(), Version = checked(goal.Version + 1) } : goal, ct));
    }

    /// <summary>Pauses goal pursuit before signaling only its admitted worker.</summary>
    [HttpPost("threads/{threadId}/goal/pause")]
    public async Task<IActionResult> PauseGoal(string threadId, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(scope.UserId, threadId, ct);
        if (thread is null) { return NotFound(); }

        string? runId = null;
        await scope.ConversationGoals.UpdateAsync(scope.UserId, thread, goal =>
        {
            if (goal?.Status != ChatGoalStatus.Active) { return goal; }

            runId = goal.RunId;
            return goal.WithStatus(ChatGoalStatus.Paused, "Paused");
        }, ct);
        if (runId is not null) { threadRunGuard.CancelExact(scope.UserId, threadId, runId); }

        return NoContent();
    }

    /// <summary>Resumes explicitly with a fresh cycle allowance and the existing spending history.</summary>
    [HttpPost("threads/{threadId}/goal/resume")]
    public async Task<ActionResult<ChatGoal>> ResumeGoal(string threadId, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(scope.UserId, threadId, ct);
        if (thread is null) { return NotFound(); }

        var existing = await scope.ConversationGoals.GetAsync(scope.UserId, thread, ct);
        if (existing is null) { return NotFound(); }

        if (existing.Status == ChatGoalStatus.Achieved) { return Conflict("Clear the achieved goal before starting another."); }

        if (thread.DisplayMessages.Any(message => message.Segments.Any(segment =>
            UserQuestions.TryRead(message, segment, out var card) && UserQuestions.GetState(thread, card!) == UserQuestions.CardState.Pending)))
        {
            return Conflict("Answer or dismiss the pending question before resuming the goal.");
        }

        await scope.AgentGoals.LoadAsync();
        await scope.ConversationGoals.UpdateAsync(scope.UserId, thread, goal =>
            goal?.Id == existing.Id && goal.Status is ChatGoalStatus.Paused or ChatGoalStatus.Blocked
                ? goal with { Status = ChatGoalStatus.Active, Reason = null, Iterations = 0,
                    MaxIterations = scope.AgentGoals.GetLimit(goal.AgentId!) ?? int.MaxValue, RunId = null, ActiveSince = null }
                : goal, ct);
        await chatRunHost.TryStartGoalAsync(scope.UserId, threadId, ct);
        return Ok(await scope.ConversationGoals.GetAsync(scope.UserId, thread, ct));
    }

    /// <summary>Removes continuation while allowing current work and queued human turns to finish.</summary>
    [HttpDelete("threads/{threadId}/goal")]
    public async Task<IActionResult> ClearGoal(string threadId, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        var thread = await scope.ChatThreadRepository.GetThreadAsync(scope.UserId, threadId, ct);
        if (thread is null) { return NotFound(); }

        await scope.ConversationGoals.DeleteAsync(scope.UserId, thread, ct);
        return NoContent();
    }

    [HttpPost("threads")]
    public async Task<ActionResult<ChatThread>> CreateThread([FromBody] CreateThreadRequest? request, CancellationToken ct)
    {
        try
        {
            var thread = await chatSessionService.CreateThreadAsync(GetUserId(), request?.AgentId, request?.ProjectId, ct);
            return Ok(thread);
        }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }
    }

    [HttpPut("threads/{threadId}")]
    public async Task<IActionResult> SaveThread(string threadId, [FromBody] ChatThread thread, CancellationToken ct, [FromQuery] bool create = false)
    {
        var userId = GetUserId();
        using var saveLease = threadRunGuard.Acquire(userId, threadId, $"save-{Guid.NewGuid():N}");
        if (saveLease is null)
        {
            return Conflict();
        }

        thread.Id = threadId;
        try
        {
            if (create)
            {
                await chatSessionService.CreateThreadAsync(userId, thread, ct);
            }
            else
            {
                await chatSessionService.SaveThreadAsync(userId, thread, ct);
            }
        }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }

        return NoContent();
    }

    /// <summary>Branches an owned completed text reply into a saved independent chat.</summary>
    [HttpPost("threads/{threadId}/branch/{messageId}")]
    public async Task<ActionResult<ChatThread>> BranchThread(string threadId, string messageId, CancellationToken ct)
    {
        var userId = GetUserId();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var scope = await GetUserScopeAsync(linked.Token);
            var source = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, linked.Token);
            if (source is null) { return NotFound(); }

            var rootId = source.BranchOrigin?.RootThreadId ?? source.Id;
            // Wait for family numbering before holding a source lease that user-wide deletion must drain.
            using var familyLease = await threadRunGuard.AcquireAsync(userId,
                $"branch-family-{rootId}", $"branch_{Guid.NewGuid():N}", linked.Token);
            using var lease = threadRunGuard.Acquire(userId, threadId, $"branch_{Guid.NewGuid():N}");
            if (lease is null) { return Conflict(); }

            using var cancellation = lease.CancellationToken.Register(linked.Cancel);
            source = await scope.ChatThreadRepository.GetThreadAsync(userId, threadId, linked.Token);
            if (source is null) { return NotFound(); }

            if ((source.BranchOrigin?.RootThreadId ?? source.Id) != rootId) { return Conflict(); }

            var child = await chatSessionService.BranchThreadAsync(userId, threadId, messageId, linked.Token);
            return child is null ? NotFound() : Ok(child);
        }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }
        catch (ArgumentException) { return BadRequest(); }
        catch (InvalidOperationException ex) { return Conflict(new ProblemDetails { Title = ex.Message }); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return Conflict(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to branch chat {ThreadId} at reply {MessageId} for user {UserId}.",
                threadId, messageId, userId);
            return Problem(title: "The chat could not be branched. Try again.");
        }
    }

    /// <summary>Lists existing authorized folders for a General conversation.</summary>
    [HttpGet("threads/{threadId}/roots")]
    [HttpGet("agents/{agentId}/roots")]
    [HttpGet("roots")]
    public async Task<IActionResult> FileRoots(string? threadId, CancellationToken ct, string? agentId = null)
    {
        try { return Ok(ProjectAccessService.GetRoots(await GetGeneralDirectoriesAsync(threadId, ct, agentId))); }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }
    }

    /// <summary>Lists bounded direct children for a General conversation.</summary>
    [HttpGet("threads/{threadId}/files")]
    [HttpGet("agents/{agentId}/files")]
    [HttpGet("files")]
    public async Task<IActionResult> Files(string? threadId, string rootId, string? path, CancellationToken ct, string? agentId = null)
    {
        try
        {
            ApplyPrivateFileHeaders();
            return Ok(ProjectAccessService.ListFiles(await GetGeneralDirectoriesAsync(threadId, ct, agentId), rootId, path));
        }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }
    }

    /// <summary>Previews bounded text for a General conversation.</summary>
    [HttpGet("threads/{threadId}/file")]
    [HttpGet("agents/{agentId}/file")]
    [HttpGet("file")]
    public async Task<IActionResult> FilePreview(string? threadId, string rootId, string path, CancellationToken ct, string? agentId = null)
    {
        try
        {
            ApplyPrivateFileHeaders();
            var directories = await GetGeneralDirectoriesAsync(threadId, ct, agentId);
            var root = ProjectAccessService.SelectRoot(directories, rootId);
            return Ok(await editing.ReadAsync(GetUserId(), new(ProjectAccessService.ResolveRelativePath(root, path), directories), ct));
        }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }
    }

    /// <summary>Downloads the original file within the selected General agent's current grants.</summary>
    [HttpGet("threads/{threadId}/download")]
    [HttpGet("agents/{agentId}/download")]
    [HttpGet("download")]
    public async Task<IActionResult> Download(string? threadId, string rootId, string path, CancellationToken ct, string? agentId = null)
    {
        try
        {
            ApplyPrivateFileHeaders();
            var directories = await GetGeneralDirectoriesAsync(threadId, ct, agentId);
            var root = ProjectAccessService.SelectRoot(directories, rootId);
            var target = new WorkspaceFileTarget(ProjectAccessService.ResolveRelativePath(root, path), directories);
            return File(await files.OpenReadAsync(GetUserId(), target, ct), "application/octet-stream",
                path.Split('/').Last(), enableRangeProcessing: true);
        }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }
    }

    /// <summary>Saves existing General-chat text under the current grants and stored revision.</summary>
    [HttpPut("threads/{threadId}/file")]
    [HttpPut("agents/{agentId}/file")]
    [HttpPut("file")]
    public async Task<IActionResult> SaveFile(string? threadId, string rootId, string path,
        SaveProjectFileRequest request, CancellationToken ct, string? agentId = null)
    {
        try
        {
            ApplyPrivateFileHeaders();
            var directories = await GetGeneralDirectoriesAsync(threadId, ct, agentId);
            var root = ProjectAccessService.SelectRoot(directories, rootId);
            var result = await editing.SaveAsync(GetUserId(),
                new(ProjectAccessService.ResolveRelativePath(root, path), directories), request, ct);
            return result.Conflict ? Conflict(result) : Ok(result);
        }
        catch (Exception ex) when (TryProjectProblem(ex, out var response)) { return response; }
    }

    private async Task<IReadOnlyList<AllowedDirectory>> GetGeneralDirectoriesAsync(string? threadId, CancellationToken ct, string? agentId = null)
    {
        var scope = await GetUserScopeAsync(ct);
        var thread = threadId is null ? new ChatThread { AgentId = agentId }
            : await scope.ChatThreadRepository.GetThreadAsync(scope.UserId, threadId, ct);
        if (thread is null || thread.ProjectId is not null) { throw new KeyNotFoundException(); }

        return await scope.ProjectAccess.ResolveThreadDirectoriesAsync(thread, scope.CrewService, scope.AgentDefinitionStore, ct: ct);
    }

    private bool TryProjectProblem(Exception exception, out ObjectResult response)
    {
        var status = exception switch
        {
            KeyNotFoundException or FileNotFoundException or DirectoryNotFoundException => StatusCodes.Status404NotFound,
            UnauthorizedAccessException => StatusCodes.Status403Forbidden,
            ArgumentException or InvalidOperationException => StatusCodes.Status400BadRequest,
            IOException or InvalidDataException => StatusCodes.Status503ServiceUnavailable,
            _ => 0
        };
        if (status == 0) { response = null!; return false; }

        logger.LogWarning(exception, "Conversation project access failed for user {UserId}.", GetUserId());
        response = Problem(statusCode: status, detail: status == StatusCodes.Status404NotFound
            ? "The conversation or folder is unavailable." : exception.Message);
        return true;
    }

    /// <summary>Renames one conversation owned by the signed-in user.</summary>
    [HttpPut("threads/{threadId}/title")]
    public async Task<IActionResult> RenameThread(
        string threadId,
        [FromBody] RenameThreadRequest? request,
        CancellationToken ct)
    {
        var title = request?.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > RenameThreadRequest.MaxTitleLength)
        {
            return BadRequest();
        }

        var userId = GetUserId();
        using var renameLease = threadRunGuard.Acquire(userId, threadId, $"rename-{Guid.NewGuid():N}");
        if (renameLease is null)
        {
            return Conflict();
        }

        try
        {
            return await chatSessionService.RenameThreadAsync(userId, threadId, title, ct)
                ? NoContent()
                : NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
    }

    [HttpDelete("threads/{threadId}")]
    public async Task<IActionResult> DeleteThread(string threadId, CancellationToken ct)
    {
        var userId = GetUserId();
        await chatSessionService.DeleteThreadAsync(userId, threadId, ct);
        return NoContent();
    }

    [HttpDelete("threads")]
    public async Task<IActionResult> DeleteAllThreads(CancellationToken ct)
    {
        var userId = GetUserId();
        using var deletionLease = await threadRunGuard.CancelAndAcquireUserAsync(userId, ct);
        await chatSessionService.DeleteAllThreadsAsync(userId, ct);
        projectionStore.RemoveUser(userId);
        return NoContent();
    }

    [HttpPost("elicitations/{requestId:guid}/boolean")]
    public async Task<IActionResult> SubmitBooleanElicitation(
        Guid requestId,
        [FromBody] SubmitBooleanElicitationRequest request,
        CancellationToken ct)
    {
        try
        {
            await chatSessionService.SubmitBooleanElicitationAsync(GetUserId(), requestId, request.Value, request.IsCancel, ct);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResult { Message = ex.Message }); }
    }

    [HttpPost("elicitations/{requestId:guid}/string")]
    public async Task<IActionResult> SubmitStringElicitation(
        Guid requestId,
        [FromBody] SubmitStringElicitationRequest request,
        CancellationToken ct)
    {
        try
        {
            await chatSessionService.SubmitStringElicitationAsync(GetUserId(), requestId, new Dictionary<string, string>(request.Values, StringComparer.OrdinalIgnoreCase), request.IsCancel, ct);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResult { Message = ex.Message }); }
    }

    [HttpPost("elicitations/{requestId:guid}/number")]
    public async Task<IActionResult> SubmitNumberElicitation(
        Guid requestId,
        [FromBody] SubmitNumberElicitationRequest request,
        CancellationToken ct)
    {
        try
        {
            await chatSessionService.SubmitNumberElicitationAsync(GetUserId(), requestId, new Dictionary<string, NumberElicitationValue>(request.Values, StringComparer.OrdinalIgnoreCase), request.IsCancel, ct);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResult { Message = ex.Message }); }
    }

    [HttpPost("elicitations/{requestId:guid}/enum")]
    public async Task<IActionResult> SubmitEnumElicitation(
        Guid requestId,
        [FromBody] SubmitEnumElicitationRequest request,
        CancellationToken ct)
    {
        try
        {
            await chatSessionService.SubmitEnumElicitationAsync(GetUserId(), requestId, new Dictionary<string, string>(request.Values, StringComparer.OrdinalIgnoreCase), request.IsCancel, ct);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new ErrorResult { Message = ex.Message }); }
    }

    /// <summary>Reads only permissions belonging to the authenticated conversation owner.</summary>
    [HttpGet("threads/{threadId}/permissions")]
    public async Task<ActionResult<ChatPermissions>> GetPermissions(string threadId, CancellationToken ct)
    {
        try { return Ok(await chatSessionService.GetPermissionsAsync(GetUserId(), threadId, ct)); }
        catch (KeyNotFoundException ex) { return NotFound(new ErrorResult { Message = ex.Message }); }
        // A saved project's folder can disappear between runs.
        catch (ArgumentException ex) { return BadRequest(new ErrorResult { Message = ex.Message }); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    /// <summary>Changes the next-run preset without accepting browser-supplied execution authority.</summary>
    [HttpPut("threads/{threadId}/permissions")]
    public async Task<ActionResult<ChatPermissions>> SavePermissions(string threadId, UpdateChatPermissionsRequest request, CancellationToken ct)
    {
        try { return Ok(await chatSessionService.SavePermissionsAsync(GetUserId(), threadId, request.Preset, ct)); }
        catch (KeyNotFoundException ex) { return NotFound(new ErrorResult { Message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResult { Message = ex.Message }); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    /// <summary>Revokes one grant or all grants in the owned conversation.</summary>
    [HttpDelete("threads/{threadId}/permissions/grants/{grantId?}")]
    public async Task<IActionResult> RevokePermission(string threadId, string? grantId, CancellationToken ct)
    {
        try { await chatSessionService.RevokePermissionAsync(GetUserId(), threadId, grantId, ct); return NoContent(); }
        catch (KeyNotFoundException ex) { return NotFound(new ErrorResult { Message = ex.Message }); }
    }

    [HttpPost("tool-approvals/{requestId}")]
    public async Task<IActionResult> SubmitToolApproval(
        string requestId,
        [FromBody] SubmitToolApprovalRequest request,
        CancellationToken ct)
    {
        try { await chatSessionService.SubmitToolApprovalAsync(GetUserId(), requestId, request.Decision, ct); return NoContent(); }
        catch (ArgumentException ex) { return BadRequest(new ErrorResult { Message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new ErrorResult { Message = ex.Message }); }
    }
}
