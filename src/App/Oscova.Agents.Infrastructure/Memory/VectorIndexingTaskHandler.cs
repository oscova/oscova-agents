using Oscova.Agents.Core.Localization;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Llm;
using Oscova.Agents.Tasks;
using Oscova.Agents.Infrastructure.Metrics;
using Oscova.Agents.Core.Conversation;

namespace Oscova.Agents.Infrastructure.Memory;

/// <summary>
/// Executes the reserved vector-indexing task without invoking an LLM.
/// </summary>
public sealed class VectorIndexingTaskHandler(
    UserServiceManager userServiceManager,
    IModelProviderCatalog modelProviderCatalog,
    ModelBudgetService budgets,
    RunContextAccessor runs) : IDeterministicTaskHandler
{
    public const string DefaultInstruction = "Index new conversation messages into the vector store.";

    public async Task ExecuteAsync(AgentTaskDefinition task, CancellationToken ct = default)
    {
        var userId = task.UserId;
        var userScope = await userServiceManager.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var config = userScope.ModelProviderSettings.SelectedEmbeddingModelProvider;
        var provider = config is null ? null : modelProviderCatalog.Find(config.Type);
        if (EmbeddingGeneratorFactory.CreateVectorSpace(config) is null || provider?.SupportsEmbedding != true)
        {
            // Optional embedding setup is recorded in task history only. The task executor posts no notice for it.
            throw new AgentTaskSkippedException(
                NotificationTextKeys.TaskMissingEmbeddingProvider,
                "No embedding model provider is configured. Set one up in Settings > Agents & AI > Model Providers > Embedding Provider to run this task.");
        }

        if (await budgets.EmbeddingPauseAsync(userId, config!.Id, ct).ConfigureAwait(false) is not null)
        {
            throw new AgentTaskSkippedException(NotificationTextKeys.ModelBudgetTaskSkipped,
                "The model budget has been reached. This attempt was skipped. The task remains scheduled.");
        }

        if (runs.Current is { } context) { context.AdmittedEmbeddingProviderId = config.Id; }

        // The encrypted index owns progress, so lost vectors can replay already-watermarked threads.
        var threadSummaries = await userScope.ChatThreadRepository
            .GetThreadsAsync(userId, ct: ct)
            .ConfigureAwait(false);

        foreach (var summary in threadSummaries)
        {

            var thread = await userScope.ChatThreadRepository.GetThreadAsync(userId, summary.Id, ct).ConfigureAwait(false);
            if (thread is null)
            {
                continue;
            }

            var agentId = string.IsNullOrWhiteSpace(thread.AgentId)
                ? userScope.AppService.MainAgentDefinition.Id
                : thread.AgentId;
            var agentDefinition = userScope.AppService.ResolveAgentDefinition(agentId);
            if (!agentDefinition.IsMemoryEnabled)
            {
                continue;
            }

            var requireLocalEndpoint = userScope.PrivacySettings.IsLocalOnly || agentDefinition.IsLocalOnly;

            await userScope.ConversationIndexingService.IndexUnflushedMessagesAsync(
                userId,
                agentId,
                thread,
                agentDefinition.IsMemoryEnabled,
                requireLocalEndpoint: requireLocalEndpoint,
                ct: ct).ConfigureAwait(false);
        }
    }
}
