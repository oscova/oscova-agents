using Oscova.Agents.Memory;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Infrastructure.Memory;

/// <summary>Skips Dreaming before any model call when the recent daily logs it promotes from are empty.</summary>
public sealed class DreamingTaskHandler(IAgentMemoryService memory) : IAgentTaskHandler
{
    /// <inheritdoc />
    public async Task<AgentRunPlan?> PrepareAsync(AgentTaskDefinition task, CancellationToken ct = default)
    {
        var today = (await memory.ReadDailyLogAsync(task.UserId, ct: ct).ConfigureAwait(false)).Date;
        var store = memory.GetStore(task.UserId);

        // A log read also returns its metadata, so emptiness is judged on the saved bodies.
        var hasInput = await store.HasBodyAsync(AgentMemoryService.DailyConceptId(today), ct).ConfigureAwait(false) ||
            await store.HasBodyAsync(AgentMemoryService.DailyConceptId(today.AddDays(-1)), ct).ConfigureAwait(false);
        return hasInput ? new AgentRunPlan(task.Instruction) : null;
    }
}
