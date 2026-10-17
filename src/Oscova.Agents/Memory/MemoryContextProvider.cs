using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Templating;
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
/// <param name="memoryTools">Memory tools exposed when the memory provider is enabled for an agent.</param>
public sealed class MemoryContextProvider(
    IAgentMemoryService memoryService,
    string userId,
    IReadOnlyList<AITool>? memoryTools = null) : AIContextProvider
{
    private readonly IReadOnlyList<AITool> _memoryTools = memoryTools ?? [];

    /// <summary>
    /// Default prompt template that explains persistent memory and inserts the assembled memory block.
    /// </summary>
    // Must stay identical to the seeded prompts/memory-system.md; SeedContentProviderTests pins the pair.
    public const string DefaultMemoryPrompt = """
        You have persistent user memory. Your entry index and recent daily logs are below.

        - Save new durable user facts, preferences and decisions with memory__save.
        - Save temporary or short-term information only with memory__save_daily.
        - Find/read existing concepts before updating. Skip unchanged information.
        - Reuse discovered IDs. New concepts need a relative ID and frontmatter.type.
        - Save detail first, then link its returned reference from the summary.
        - Use memory__search/get for past conversations.
        - Skip routine progress and details recoverable from project files.
        - Do not infer verification from requests or assistant claims.

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
            : longTermMemory;

        var agentMemoryXml = BuildAgentMemoryXml(longTermSection, todayLog.Content, yesterdayLog.Content, todayLog.Date, yesterdayLog.Date);
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

}
