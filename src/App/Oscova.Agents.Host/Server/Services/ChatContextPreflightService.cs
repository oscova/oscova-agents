using Oscova.Agents.Conversations;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Definitions;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Llm;
using Oscova.Agents.Providers;

#pragma warning disable MAAI001

namespace Oscova.Agents.Host.Server.Services;

/// <summary>
/// Estimates whether a pending web-chat prompt will fit the selected model before a run starts.
/// </summary>
public sealed class ChatContextPreflightService(
    UserServiceManager userServiceManager,
    ChatSessionService chatSessionService,
    ChatAttachmentManifestEstimator attachmentManifestEstimator,
    RunContextAccessor runContextAccessor,
    ChatAttachmentManifestBuilder attachmentManifestBuilder)
{
    private const string InstructionsKey = "instructions";
    private const string PriorHistoryKey = "prior-history";
    private const string CurrentDraftKey = "current-draft";
    private const string AttachmentsKey = "attachments";
    private const string ContextProvidersKey = "context-providers";
    private const string ToolsKey = "tools";
    private const string OutputReserveKey = "output-reserve";

    /// <summary>Estimates the selected chat context and optional draft against the configured model window.</summary>
    public async Task<ChatContextPreflightResponse> EstimateAsync(
        string userId,
        string threadId,
        ChatContextPreflightRequest request,
        CancellationToken ct = default)
    {
        if (!ChatAttachmentLimits.AreWorkspaceFilesValid(request.WorkspaceFiles)) { throw new ArgumentException("Invalid workspace file references."); }

        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        userScope.AppService.SyncTools();
        await userScope.AppService.ReloadAgentDefinitionsAsync().ConfigureAwait(false);

        var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, threadId, ct).ConfigureAwait(false);
        if (thread is null && await userScope.ChatThreadRepository.ThreadExistsAsync(userId, threadId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Unlock the confidential conversation before continuing.");
        }

        if (thread is not null && !string.Equals(thread.ProjectId, request.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A saved conversation cannot change project.");
        }

        thread ??= new ChatThread { Id = threadId, ProjectId = request.ProjectId };
        var ownerDefinition = userScope.AppService.ResolveAgentDefinition(
            AgentSelectionResolver.ResolveAgentId(
                request.AgentId,
                thread.AgentId,
                thread.DisplayMessages.Count > 0));
        var agentDefinition = await AgentSelectionResolver.ResolveRecipientAsync(userScope, thread, ownerDefinition, request.RecipientAgentId, ct).ConfigureAwait(false);
        var providerId = AgentSelectionResolver.ResolveModelProviderId(ownerDefinition, agentDefinition, request.ModelProviderId,
            userScope.ModelProviderSettings.SelectedChatModelProviderId?.ToString());
        var standardDirectories = userScope.ProjectAccess.ResolveRunDirectories(
            thread.ProjectId, userScope.ExecutionSettings.ProviderId);
        await using var previewScope = runContextAccessor.SetContext(new RunContext
        {
            UserId = userId,
            ExecutionId = threadId,
            ExecutionKind = RunExecutionKind.WebChat,
            ProjectId = thread.ProjectId,
            IsIncognito = thread.IsIncognito,
            ExecutionProviderId = userScope.ExecutionSettings.ProviderId,
            AirGapped = userScope.PrivacySettings.IsLocalOnly || ownerDefinition.IsLocalOnly || agentDefinition.IsLocalOnly,
            CurrentAgentId = agentDefinition.Id,
            CurrentAgentName = agentDefinition.Name,
            StandardRunDirectories = standardDirectories,
            AllowedDirectories = userScope.CrewService.ResolveAllowedDirectories(userId, agentDefinition, standardDirectories)
        });
        ModelProviderConfig selectedProvider;
        (IChatClient Client, ModelProviderConfig EffectiveProvider) clientResolution;
        try
        {
            selectedProvider = userScope.AppService.ResolveChatModelProvider(agentDefinition, providerId);
            clientResolution = await userScope.AppService.GetChatClientResolutionAsync(
                agentDefinition,
                selectedProvider).ConfigureAwait(false);
        }
        catch (ModelProviderUnavailableException)
        {
            // Passive context metadata must not make provider readiness a page-health dependency.
            return new ChatContextPreflightResponse { IsContextWindowKnown = false };
        }

        var effectiveProvider = clientResolution.EffectiveProvider;
        if (effectiveProvider.ContextWindowSize <= 0)
        {
            return new ChatContextPreflightResponse
            {
                IsContextWindowKnown = false,
                ProviderName = effectiveProvider.Name,
                ModelId = effectiveProvider.ModelId
            };
        }

        var effectiveChatOptions = effectiveProvider.ChatOptions.Clone();
        var outputReserve = ContextWindowBudget.DeriveOutputReserve(
            effectiveProvider.ContextWindowSize,
            effectiveChatOptions.MaxOutputTokens);
        var availableInput = Math.Max(0, effectiveProvider.ContextWindowSize - outputReserve);
        var extraProviders = ResolveExtraProviders(effectiveProvider, clientResolution.Client);
        var previewAgent = await ResolvePreviewAgentAsync(
            userScope,
            agentDefinition,
            selectedProvider,
            providerId,
            extraProviders).ConfigureAwait(false);
        var messageBuild = await BuildMessagesAsync(userScope, thread, request, agentDefinition, previewAgent, ct).ConfigureAwait(false);
        var preview = await userScope.CrewService.PreviewContextAsync(
            userId,
            agentDefinition,
            messageBuild.Messages,
            extraProviders,
            previewAgent,
            messageBuild.Session,
            ct).ConfigureAwait(false);

        var breakdown = BuildBreakdown(messageBuild, preview, outputReserve);
        var estimatedInputTokens = breakdown
            .Where(static item => item.Key != OutputReserveKey)
            .Sum(static item => item.EstimatedTokens);
        var overage = Math.Max(0, estimatedInputTokens - availableInput);

        return new ChatContextPreflightResponse
        {
            IsContextWindowKnown = true,
            IsOverBudget = overage > 0,
            ContextWindowSize = effectiveProvider.ContextWindowSize,
            AvailableInputTokens = availableInput,
            EstimatedInputTokens = estimatedInputTokens,
            ReservedOutputTokens = outputReserve,
            OverageTokens = overage,
            ProviderName = effectiveProvider.Name,
            ModelId = effectiveProvider.ModelId,
            Breakdown = breakdown
        };
    }

    private async Task<MessageBuildResult> BuildMessagesAsync(
        UserServiceScope userScope,
        ChatThread thread,
        ChatContextPreflightRequest request,
        AgentDefinition definition,
        ChatClientAgent previewAgent,
        CancellationToken ct)
    {
        var snapshot = await chatSessionService.GetUnredactedContextSnapshotAsync(
            userScope,
            thread,
            request.ModelProviderId,
            ct).ConfigureAwait(false);
        var priorMessages = snapshot.Messages.Count > 0
            ? snapshot.Messages.ToList()
            : BuildDisplayMessageFallback(thread);
        var state = ChatParticipantSessions.Read(thread.SessionElement);
        var participant = state.Join(definition.Id);
        var session = snapshot.Session;
        if (definition.Id != thread.AgentId && !string.IsNullOrEmpty(request.RecipientAgentId))
        {
            session = participant.Session is { } saved
                ? await previewAgent.DeserializeSessionAsync(saved, cancellationToken: ct).ConfigureAwait(false)
                : await previewAgent.CreateSessionAsync(ct).ConfigureAwait(false);
            priorMessages = session.TryGetInMemoryChatHistory(out var guestHistory) ? guestHistory.ToList() : [];
        }

        priorMessages.AddRange(ChatParticipantSessions.GetSharedMessages(thread, participant));
        var attachments = request.Attachments ?? [];
        var promptText = request.PromptText ?? string.Empty;
        var attachmentManifest = attachmentManifestEstimator.Build(attachments);
        if (!string.IsNullOrEmpty(request.RecipientAgentId))
        {
            var earlier = thread.DisplayMessages.SelectMany(message => message.Attachments).DistinctBy(attachment => attachment.Id).ToArray();
            attachmentManifest = attachmentManifestBuilder.Compose(attachmentManifest, earlier, thread.ProjectId is not null &&
                (await userScope.ProjectAccess.AuthorizeAsync(thread.ProjectId, ct).ConfigureAwait(false)).NodeId is not null);
        }

        attachmentManifest = ChatDisplayMessageFormatter.ComposeFiles(attachmentManifest, request.WorkspaceFiles);
        var userText = ComposeUserText(promptText, attachmentManifest);

        var messages = priorMessages.ToList();
        if (!string.IsNullOrWhiteSpace(userText))
        {
            messages.Add(new ChatMessage(ChatRole.User, userText));
        }

        return new MessageBuildResult(
            messages,
            priorMessages,
            promptText,
            attachmentManifest,
            session);
    }

    private IReadOnlyList<AIContextProvider> ResolveExtraProviders(
        ModelProviderConfig effectiveProvider,
        IChatClient chatClient)
    {
        if (!effectiveProvider.SupportsImages)
        {
            return [];
        }

        return [new VisionToolProvider(
            chatClient,
            () => runContextAccessor.Current?.AllowedDirectories ?? [])];
    }

    private static async Task<ChatClientAgent> ResolvePreviewAgentAsync(
        UserServiceScope userScope,
        AgentDefinition agentDefinition,
        ModelProviderConfig selectedProvider,
        string? explicitModelProviderId,
        IReadOnlyList<AIContextProvider> extraProviders)
    {
        var modelProviderId = string.IsNullOrWhiteSpace(explicitModelProviderId)
            ? selectedProvider.Id.ToString()
            : explicitModelProviderId;

        return await userScope.AppService.GetOrCreateAgent(
            userScope.UserId,
            agentDefinition,
            modelProviderId,
            extraProviders: extraProviders).ConfigureAwait(false);
    }

    private static IReadOnlyList<ChatContextPreflightBreakdownItem> BuildBreakdown(
        MessageBuildResult messageBuild,
        AgentContextPreview preview,
        int outputReserve)
    {
        var baseMessageTokens = ContextWindowTokenEstimator.EstimateMessages(messageBuild.Messages);
        var previewMessageTokens = ContextWindowTokenEstimator.EstimateMessages(preview.Messages);
        var providerAddedMessageTokens = Math.Max(0, previewMessageTokens - baseMessageTokens);

        return
        [
            new ChatContextPreflightBreakdownItem
            {
                Key = InstructionsKey,
                EstimatedTokens = ContextWindowTokenEstimator.EstimateText(preview.Instructions)
            },
            new ChatContextPreflightBreakdownItem
            {
                Key = PriorHistoryKey,
                EstimatedTokens = ContextWindowTokenEstimator.EstimateMessages(messageBuild.PriorMessages)
            },
            new ChatContextPreflightBreakdownItem
            {
                Key = CurrentDraftKey,
                EstimatedTokens = ContextWindowTokenEstimator.EstimateText(messageBuild.PromptText)
            },
            new ChatContextPreflightBreakdownItem
            {
                Key = AttachmentsKey,
                EstimatedTokens = ContextWindowTokenEstimator.EstimateText(messageBuild.AttachmentManifest)
            },
            new ChatContextPreflightBreakdownItem
            {
                Key = ContextProvidersKey,
                EstimatedTokens = providerAddedMessageTokens
            },
            new ChatContextPreflightBreakdownItem
            {
                Key = ToolsKey,
                EstimatedTokens = ContextWindowTokenEstimator.EstimateTools(preview.Tools)
            },
            new ChatContextPreflightBreakdownItem
            {
                Key = OutputReserveKey,
                EstimatedTokens = outputReserve
            }
        ];
    }

    private static List<ChatMessage> BuildDisplayMessageFallback(ChatThread thread)
    {
        var messages = new List<ChatMessage>();
        foreach (var displayMessage in thread.DisplayMessages)
        {
            var text = ChatDisplayMessageFormatter.ComposeFiles(ChatDisplayMessageFormatter.GetStorageText(displayMessage), displayMessage.WorkspaceFiles);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var role = displayMessage.From == MessageOwner.User
                ? ChatRole.User
                : ChatRole.Assistant;
            messages.Add(new ChatMessage(role, text));
        }

        return messages;
    }

    private static string ComposeUserText(string promptText, string attachmentManifest)
    {
        if (string.IsNullOrWhiteSpace(attachmentManifest))
        {
            return promptText.Trim();
        }

        if (string.IsNullOrWhiteSpace(promptText))
        {
            return attachmentManifest;
        }

        return promptText.Trim() + "\n\n" + attachmentManifest;
    }

    private sealed record MessageBuildResult(
        IReadOnlyList<ChatMessage> Messages,
        IReadOnlyList<ChatMessage> PriorMessages,
        string PromptText,
        string AttachmentManifest,
        AgentSession? Session);
}

#pragma warning restore MAAI001
