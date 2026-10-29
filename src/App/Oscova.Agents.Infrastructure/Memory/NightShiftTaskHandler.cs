using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Llm;
using Oscova.Agents.Secrets;
using Oscova.Agents.Tasks;
using Oscova.Agents.Templating;

namespace Oscova.Agents.Infrastructure.Memory;

/// <summary>Hands the oldest open to-do items the user delegated to the agent to the nightly run, and skips the night when there are none.</summary>
public sealed class NightShiftTaskHandler(IUserDirectoryService userDirectories, IContentProtector protector,
    IUserFileBackupService fileBackups) : IAgentTaskHandler
{
    // The 40-step tool loop and the per-response token budget cannot bound a run, so the item count does.
    private const int MaxItems = 3;

    /// <inheritdoc />
    public async Task<AgentRunPlan?> PrepareAsync(AgentTaskDefinition task, CancellationToken ct = default)
    {
        var store = BacklogFile.CreateStore(task.UserId, userDirectories, protector, fileBackups);
        var items = (await BacklogFile.ReadItemsAsync(store, ct).ConfigureAwait(false))
            .Where(item => item.Assignee == BacklogFile.AgentAssignee && item.State == BacklogFile.OpenState)
            .OrderBy(item => item.Added ?? DateTimeOffset.MaxValue).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(MaxItems).ToArray();
        if (items.Length == 0) { return null; }

        string Render(string evidence) => PromptTemplateRenderer.Render(task.Instruction, new { items = evidence });
        string Evidence(int count) => string.Join("\n\n", items.Take(count).Select(Describe)) +
            (count < items.Length ? $"\n\n{items.Length - count} more item(s) did not fit and stay open." : string.Empty);
        var baseTokens = ContextWindowTokenEstimator.EstimateText(Render(string.Empty));
        int EstimateEvidence(string evidence) => Math.Max(ContextWindowTokenEstimator.EstimateText(evidence),
            ContextWindowTokenEstimator.EstimateText(Render(evidence)) - baseTokens);

        return new AgentRunPlan(Render(string.Empty), FinalizeAsync: (budget, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = items.Length;
            while (count > 0 && EstimateEvidence(Evidence(count)) > budget) { count--; }

            if (count == 0)
            {
                throw new InvalidOperationException("The model context has no room for a delegated to-do item. Increase the context allowance or shorten the item.");
            }

            var evidence = Evidence(count);
            var instruction = Render(evidence);
            if (!instruction.Contains(evidence, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The task instruction must include its complete items placeholder.");
            }

            return Task.FromResult(new AgentRunPlan(instruction));
        });
    }

    private static string Describe(BacklogItem item) =>
        $"[Item {item.Id}] {item.Title}\nAdded {BacklogItem.Format(item.Added)}" +
        (item.Description.Length > 0 ? $"\n{item.Description}" : string.Empty) +
        (item.Body.Trim().Length > 0 ? $"\n{item.Body.Trim()}" : string.Empty);
}
