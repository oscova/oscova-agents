using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Templating;
using Oscova.Agents.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Memory;

/// <summary>
/// Boot-loads the user's long-term memory index and recent daily logs into the agent's system prompt.
/// </summary>
/// <remarks>
/// Initializes a new instance of <see cref="MemoryContextProvider"/>.
/// </remarks>
/// <param name="memoryService">The memory service used to read memory files.</param>
/// <param name="userId">The user whose memory to load.</param>
/// <param name="maxTokenBudget">Maximum token budget for the combined memory content.</param>
/// <param name="memoryTools">Memory tools exposed when the memory provider is enabled for an agent.</param>
public sealed class MemoryContextProvider(
    IAgentMemoryService memoryService,
    string userId,
    int maxTokenBudget = 2000,
    IReadOnlyList<AITool>? memoryTools = null) : AIContextProvider
{
    private readonly IReadOnlyList<AITool> _memoryTools = memoryTools ?? [];

    /// <summary>
    /// Default prompt template that explains persistent memory and inserts the assembled memory block.
    /// </summary>
    // Must stay identical to the seeded prompts/memory-system.md; SeedContentProviderTests pins the pair.
    public const string DefaultMemoryPrompt = """
        You have a persistent memory system. Your memories from previous sessions are shown below.

        Use memory tools to save user facts, decisions, and preferences so they are accessible in future sessions. Save useful information proactively, especially anything the user would expect you to remember later.

        Use `memory_read` with target `longterm` to read the long-term index. Use `memory_read` with target `today`, `yesterday`, `yyyy-MM-dd`, or a lowercase topic slug to read daily logs or topic files. Use `memory_save` with target `longterm` to update the index, and include a `topic` only for durable facts that clearly belong in a focused topic file.

        {{ agent_memory }}
        """;

    /// <summary>
    /// The system prompt template injected into the agent's context when memory is loaded.
    /// Supports a <c>{{ agent_memory }}</c> Scriban template variable that expands to the assembled
    /// <c>&lt;agent_memory&gt;</c> XML block. If the variable is omitted, the block is appended automatically.
    /// </summary>
    public string SystemPrompt { get; set; } = DefaultMemoryPrompt;

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var longTermMemory = await memoryService.ReadLongTermMemoryAsync(userId, cancellationToken).ConfigureAwait(false);
        var todayLog = await memoryService.ReadDailyLogAsync(userId, ct: cancellationToken).ConfigureAwait(false);
        var yesterdayLog = await memoryService.ReadDailyLogAsync(userId, daysAgo: 1, ct: cancellationToken).ConfigureAwait(false);

        var longTermSection = string.IsNullOrWhiteSpace(longTermMemory)
            ? "Empty - no long-term memories saved yet."
            : ContextTextBudget.Truncate(longTermMemory, maxTokenBudget);

        var remainingBudget = maxTokenBudget - AgentTokenCounter.CountTokens(longTermSection);

        var todaySection = BuildDailySection(todayLog.Content, ref remainingBudget);
        var yesterdaySection = BuildDailySection(yesterdayLog.Content, ref remainingBudget);

        var agentMemoryXml = BuildAgentMemoryXml(longTermSection, todaySection, yesterdaySection, todayLog.Date, yesterdayLog.Date);
        var rendered = PromptTemplateRenderer.Render(SystemPrompt, new { agent_memory = agentMemoryXml });
        var instructions = rendered.Contains(agentMemoryXml) ? rendered : $"{rendered}\n\n{agentMemoryXml}";

        return new AIContext
        {
            Instructions = instructions,
            Tools = _memoryTools
        };
    }

    private static string BuildAgentMemoryXml(string longTermSection, string? todaySection, string? yesterdaySection, DateOnly today, DateOnly yesterday)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<agent_memory>")
            .AppendLine("  <long_term_index>")
            .AppendLine(longTermSection)
            .AppendLine("  </long_term_index>");

        if (!string.IsNullOrWhiteSpace(todaySection))
        {
            sb.AppendLine($"  <daily_log date=\"{today:yyyy-MM-dd}\">")
                .AppendLine(todaySection)
                .AppendLine("  </daily_log>");
        }

        if (!string.IsNullOrWhiteSpace(yesterdaySection))
        {
            sb.AppendLine($"  <daily_log date=\"{yesterday:yyyy-MM-dd}\">")
                .AppendLine(yesterdaySection)
                .AppendLine("  </daily_log>");
        }

        sb.Append("</agent_memory>");
        return sb.ToString();
    }

    private static string? BuildDailySection(string? content, ref int remainingBudget)
    {
        if (string.IsNullOrWhiteSpace(content) || remainingBudget <= 0)
        {
            return null;
        }

        var truncated = ContextTextBudget.Truncate(content, remainingBudget);
        remainingBudget -= AgentTokenCounter.CountTokens(truncated);
        return truncated;
    }
}
