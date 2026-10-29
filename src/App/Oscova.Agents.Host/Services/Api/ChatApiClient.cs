using Oscova.Agents.Conversations;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Host.Constants;
using System.Net;

namespace Oscova.Agents.Host.Services.Api;

public sealed class ChatApiClient(HttpClient httpClient) : ApiClientBase
{
    /// <summary>Reads saved goal controls without loading the model transcript.</summary>
    public async Task<ChatGoal?> GetGoalAsync(string threadId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Chat.Goal(threadId), ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) { return null; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatGoal>(ct).ConfigureAwait(false);
    }

    /// <summary>Creates or edits an objective as a control action.</summary>
    public async Task<ChatGoal> SaveGoalAsync(string threadId, SaveChatGoalRequest request,
        bool editing, CancellationToken ct = default)
    {
        using var response = editing
            ? await httpClient.PutAsJsonAsync(ApiRoutes.Chat.Goal(threadId), request, ct).ConfigureAwait(false)
            : await httpClient.PostAsJsonAsync(ApiRoutes.Chat.Goal(threadId), request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatGoal>(ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The saved goal was not returned.");
    }

    /// <summary>Pauses future goal work and its current admitted execution.</summary>
    public async Task PauseGoalAsync(string threadId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Chat.Goal(threadId) + "/pause", null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Explicitly resumes continuation after a pause or blocker.</summary>
    public async Task ResumeGoalAsync(string threadId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Chat.Goal(threadId) + "/resume", null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Removes the goal while preserving current work and ordinary queued turns.</summary>
    public async Task ClearGoalAsync(string threadId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Chat.Goal(threadId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Reads the provider budget status displayed with the chat composer.</summary>
    public async Task<IReadOnlyList<ModelBudgetStatus>> GetModelBudgetsAsync(CancellationToken ct = default) =>
        await httpClient.GetFromJsonAsync<List<ModelBudgetStatus>>(ApiRoutes.ModelProviders.Budgets, ct).ConfigureAwait(false) ?? [];

    /// <summary>Persists the client-created thread before its first run.</summary>
    public async Task CreateThreadAsync(ChatThread thread, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Chat.Thread(thread.Id) + "?create=true", thread, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Reads the server-owned preset and grants, including another tab's changes.</summary>
    public async Task<ChatPermissions?> GetPermissionsAsync(string threadId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Chat.Thread(threadId) + "/permissions", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { return null; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatPermissions>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Commits an explicit next-run preset on the authenticated conversation path.</summary>
    public async Task<ChatPermissions> SavePermissionsAsync(string threadId, ChatPermissionPreset preset, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Chat.Thread(threadId) + "/permissions",
            new UpdateChatPermissionsRequest(preset), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatPermissions>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The saved chat permissions were not returned.");
    }

    /// <summary>Removes future approval authority without stopping an already approved call.</summary>
    public async Task RevokePermissionAsync(string threadId, string? grantId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Chat.Thread(threadId) + "/permissions/grants" +
            (grantId is null ? "" : "/" + Uri.EscapeDataString(grantId)), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatThread>> GetThreadsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<ChatThread>>(ApiRoutes.Chat.Threads, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>Searches the current user's conversation titles and visible messages.</summary>
    public async Task<IReadOnlyList<string>> SearchThreadIdsAsync(string query, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<string>>(ApiRoutes.Chat.SearchThreads(query), ct)
            .ConfigureAwait(false) ?? [];
    }

    /// <summary>Returns the current user's live incognito threads.</summary>
    public async Task<IReadOnlyList<ChatThread>> GetEphemeralThreadsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<ChatThread>>(ApiRoutes.Chat.EphemeralThreads, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<ChatThread?> GetThreadAsync(string threadId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Chat.Thread(threadId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatThread>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Reads recent exchanges, an older page, or the already loaded window through its first message.</summary>
    public async Task<ChatThreadViewResponse?> GetThreadViewAsync(string threadId, CancellationToken ct = default,
        string? beforeMessageId = null, string? firstMessageId = null)
    {
        var route = ApiRoutes.Chat.Thread(threadId) + "/view";
        if (beforeMessageId is not null) { route += "?beforeMessageId=" + Uri.EscapeDataString(beforeMessageId); }

        if (firstMessageId is not null) { route += "?firstMessageId=" + Uri.EscapeDataString(firstMessageId); }

        using var response = await httpClient.GetAsync(route, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatThreadViewResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Returns the recent conversation as a bounded display view.</summary>
    public async Task<ChatThreadViewResponse?> GetRecentCurrentThreadViewAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Chat.RecentCurrentThread + "/view", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) { return null; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatThreadViewResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Creates and returns a saved branch through the selected completed reply.</summary>
    public async Task<ChatThread> BranchThreadAsync(string threadId, string messageId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Chat.BranchThread(threadId, messageId), null, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatThread>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The saved child chat was not returned.");
    }

    /// <summary>Returns the recent thread the dashboard should resume, or null when the start screen should show.</summary>
    public async Task<ChatThread?> GetRecentCurrentThreadAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Chat.RecentCurrentThread, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatThread>(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetSessionMessagesAsync(string threadId, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<ChatMessage>>(ApiRoutes.Chat.SessionMessages(threadId), ct).ConfigureAwait(false) ?? [];
    }

    public async Task<ChatSessionInspectorSnapshot> GetSessionInspectorAsync(string threadId, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<ChatSessionInspectorSnapshot>(ApiRoutes.Chat.SessionInspector(threadId), ct)
.ConfigureAwait(false) ?? new ChatSessionInspectorSnapshot();
    }

    public async Task<ChatContextPreflightResponse> GetContextPreflightAsync(
        string threadId,
        ChatContextPreflightRequest request,
        CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.Chat.ContextPreflight(threadId), request, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatContextPreflightResponse>(cancellationToken: ct)
            .ConfigureAwait(false) ?? new ChatContextPreflightResponse();
    }

    public async Task<IReadOnlyList<ChatTodoItem>> GetTodosAsync(
        string threadId,
        string? modelProviderId = null,
        CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<ChatTodoItem>>(
            ApiRoutes.Chat.Todos(threadId, modelProviderId),
            ct).ConfigureAwait(false) ?? [];
    }

    public async Task<IReadOnlyList<AgentConversationView>> GetAgentConversationsAsync(
        string threadId,
        string? modelProviderId = null,
        CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<AgentConversationView>>(
            ApiRoutes.Chat.AgentConversations(threadId, modelProviderId),
            ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>Tries to admit a web chat run without attaching the component to its execution stream.</summary>
    public async Task<bool> TryStartRunAsync(
        string threadId,
        StartChatRunRequest request,
        CancellationToken ct = default,
        Func<ModelBudgetStatus, Task<bool>>? confirmBudget = null)
    {
        using var response = await httpClient
            .PostAsJsonAsync(ApiRoutes.Chat.Runs(threadId), request, ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            var budget = await response.Content.ReadFromJsonAsync<ModelBudgetStatus>(cancellationToken: ct).ConfigureAwait(false);
            if (budget is null || confirmBudget is null || !await confirmBudget(budget).ConfigureAwait(false) || !budget.CanOverride)
            {
                return false;
            }

            request.ContinueOverBudget = true;
            return await TryStartRunAsync(threadId, request, ct).ConfigureAwait(false);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return true;
    }

    /// <summary>Sends text typed during a live run and reports whether it steered, queued, or started a turn.</summary>
    public async Task<ChatFollowUpResult> SendFollowUpAsync(
        string threadId,
        SendChatFollowUpRequest request,
        CancellationToken ct = default)
    {
        using var response = await httpClient
            .PostAsJsonAsync(ApiRoutes.Chat.FollowUps(threadId), request, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<ChatFollowUpResult>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    /// <summary>Lists the thread's queued follow-ups in run order.</summary>
    public async Task<IReadOnlyList<ChatFollowUpView>> GetFollowUpsAsync(string threadId, CancellationToken ct = default) =>
        await httpClient.GetFromJsonAsync<List<ChatFollowUpView>>(ApiRoutes.Chat.FollowUps(threadId), ct).ConfigureAwait(false) ?? [];

    /// <summary>Moves a queued follow-up into the live run.</summary>
    public async Task<ChatFollowUpResult> SteerFollowUpAsync(string threadId, string messageId, CancellationToken ct = default)
    {
        using var response = await httpClient
            .PostAsync(ApiRoutes.Chat.SteerFollowUp(threadId, messageId), null, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<ChatFollowUpResult>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    /// <summary>Removes a queued follow-up, returning false when it already left the queue.</summary>
    public async Task<bool> DeleteFollowUpAsync(string threadId, string messageId, CancellationToken ct = default)
    {
        using var response = await httpClient
            .DeleteAsync(ApiRoutes.Chat.FollowUp(threadId, messageId), ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { return false; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NoContent;
    }

    /// <summary>Closes a question card, returning false for a missing, stale, or busy conversation.</summary>
    public async Task<bool> TryDismissQuestionAsync(string threadId, string messageId, string callId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Chat.DismissQuestion(threadId, messageId, callId), null, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return false;
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NoContent;
    }

    /// <summary>Requests server-side cancellation of one exact chat run.</summary>
    public async Task StopRunAsync(string threadId, string runId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Chat.Stop(threadId),
            new StopChatRunRequest { RunId = runId },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<ChatThread> CreateThreadAsync(string? agentId, string? projectId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Chat.Threads,
            new CreateThreadRequest { AgentId = agentId, ProjectId = projectId },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChatThread>(cancellationToken: ct)
.ConfigureAwait(false) ?? new ChatThread();
    }

    public async Task SaveThreadAsync(ChatThread thread, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Chat.Thread(thread.Id), thread, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Updates the title of one chat thread owned by the current user.</summary>
    public async Task RenameThreadAsync(string threadId, string title, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            ApiRoutes.Chat.ThreadTitle(threadId),
            new RenameThreadRequest { Title = title },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Requests manual compaction of one chat session.</summary>
    public async Task<bool?> CompactThreadAsync(string threadId, Guid? modelProviderId, CancellationToken ct = default,
        Func<ModelBudgetStatus, Task<bool>>? confirmBudget = null, bool continueOverBudget = false)
    {
        var route = ApiRoutes.Chat.Compact(threadId, modelProviderId);
        if (continueOverBudget) { route += (route.Contains('?') ? "&" : "?") + "continueOverBudget=true"; }

        using var response = await httpClient.PostAsync(route, null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            var budget = await response.Content.ReadFromJsonAsync<ModelBudgetStatus>(cancellationToken: ct).ConfigureAwait(false);
            if (budget is null || confirmBudget is null || !await confirmBudget(budget).ConfigureAwait(false) || !budget.CanOverride) { return null; }

            return await CompactThreadAsync(threadId, modelProviderId, ct, continueOverBudget: true).ConfigureAwait(false);
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<bool>(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Chat.DeleteThread(threadId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task DeleteAllThreadsAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Chat.Threads, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Lightweight carrier for a web-client-side pending attachment during multipart send.</summary>
    public sealed class PendingChatAttachment
    {
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public required Stream Content { get; init; }
    }

    public async Task<IReadOnlyList<StagedAttachmentResponse>> StageAttachmentsAsync(
        string threadId,
        IReadOnlyList<PendingChatAttachment> attachments,
        CancellationToken ct = default)
    {
        if (attachments.Count == 0)
        {
            return Array.Empty<StagedAttachmentResponse>();
        }

        using var form = new MultipartFormDataContent();
        foreach (var attachment in attachments)
        {
            var fileContent = new StreamContent(attachment.Content);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(attachment.ContentType);
            form.Add(fileContent, "files", attachment.FileName);
        }

        using var response = await httpClient.PostAsync(ApiRoutes.Chat.Attachments(threadId), form, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<StagedAttachmentResponse>>(cancellationToken: ct)
.ConfigureAwait(false) ?? new List<StagedAttachmentResponse>();
    }

    public async Task SubmitBooleanElicitationAsync(Guid requestId, bool? value, bool isCancel, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Chat.BooleanElicitation(requestId),
            new SubmitBooleanElicitationRequest { Value = value, IsCancel = isCancel },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task SubmitStringElicitationAsync(Guid requestId, IReadOnlyDictionary<string, string> values, bool isCancel, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Chat.StringElicitation(requestId),
            new SubmitStringElicitationRequest
            {
                Values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
                IsCancel = isCancel
            },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task SubmitNumberElicitationAsync(Guid requestId, IReadOnlyDictionary<string, NumberElicitationValue> values, bool isCancel, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Chat.NumberElicitation(requestId),
            new SubmitNumberElicitationRequest
            {
                Values = new Dictionary<string, NumberElicitationValue>(values, StringComparer.OrdinalIgnoreCase),
                IsCancel = isCancel
            },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task SubmitEnumElicitationAsync(Guid requestId, IReadOnlyDictionary<string, string> values, bool isCancel, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Chat.EnumElicitation(requestId),
            new SubmitEnumElicitationRequest
            {
                Values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
                IsCancel = isCancel
            },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task SubmitToolApprovalAsync(string requestId, ToolApprovalDecision decision, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Chat.ToolApproval(requestId),
            new SubmitToolApprovalRequest { Decision = decision },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

}
