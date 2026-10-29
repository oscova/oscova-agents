using System.Globalization;
using System.Text;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Llm;
using Oscova.Agents.Tasks;
using Oscova.Agents.Templating;

namespace Oscova.Agents.Infrastructure.Memory;

/// <summary>Packs pending conversation evidence and commits only the text actually supplied to a learning task.</summary>
internal static class ConversationEvidence
{
    public static async Task<AgentRunPlan?> PrepareAsync(AgentTaskDefinition task, UserServiceScope scope,
        bool reflection, CancellationToken ct)
    {
        var candidates = new List<ChatThread>();
        var taskAgent = scope.AppService.ResolveAgentDefinition(task.AgentId);
        var localOnly = scope.PrivacySettings.IsLocalOnly || (task.RunScope?.IsLocalOnly ?? taskAgent.IsLocalOnly);
        foreach (var summary in await scope.ChatThreadRepository.GetThreadsAsync(task.UserId, ct: ct).ConfigureAwait(false))
        {
            if (reflection && !string.Equals(summary.AgentId, task.AgentId, StringComparison.Ordinal)) { continue; }

            var agent = scope.AppService.ResolveAgentDefinition(summary.AgentId);
            if (!agent.IsMemoryEnabled || (agent.IsLocalOnly && !localOnly)) { continue; }

            var thread = await scope.ChatThreadRepository.GetThreadAsync(task.UserId, summary.Id, ct).ConfigureAwait(false);
            if (thread is null) { continue; }

            var (count, offset) = Position(thread, reflection);
            // Messages with no usable text need no model call, but still move this task's own cursor.
            while (count < thread.DisplayMessages.Count && string.IsNullOrWhiteSpace(Line(thread, count)))
            {
                count++;
                offset = 0;
            }

            if ((count, offset) != Position(thread, reflection))
            {
                await CommitAsync(scope, thread.Id, reflection, count, offset, ct).ConfigureAwait(false);
                SetPosition(thread, reflection, count, offset);
            }

            if (count < thread.DisplayMessages.Count) { candidates.Add(thread); }
        }

        if (candidates.Count == 0) { return null; }

        var ordered = candidates.OrderBy(thread => thread.DisplayMessages[Position(thread, reflection).Count].CreatedAt)
            .ThenBy(thread => thread.Id, StringComparer.Ordinal).ToArray();
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, scope.UiSettings.GetTimeZone());
        string Render(string evidence) => reflection
            ? PromptTemplateRenderer.Render(task.Instruction, new { recent_conversation = evidence })
            : PromptTemplateRenderer.Render(task.Instruction, new
            {
                current_date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                current_time = now.ToString("HH:mm", CultureInfo.InvariantCulture),
                unsummarized_conversation = evidence
            });
        var baseTokens = ContextWindowTokenEstimator.EstimateText(Render(string.Empty));
        // Repeated evidence placeholders consume context each time they appear in the saved instruction.
        int EstimateEvidence(string evidence) => Math.Max(ContextWindowTokenEstimator.EstimateText(evidence),
            ContextWindowTokenEstimator.EstimateText(Render(evidence)) - baseTokens);

        return new AgentRunPlan(Render(string.Empty), FinalizeAsync: (budget, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Reflection is capped at 6,000 estimated evidence tokens, reduced for less available model context.
            if (reflection) { budget = Math.Min(budget, 6_000); }

            var evidence = new StringBuilder();
            var progress = new List<(string ThreadId, int Count, int Offset)>();
            foreach (var thread in ordered)
            {
                var (count, offset) = Position(thread, reflection);
                var initial = (count, offset);
                var heading = $"\n[Conversation {thread.Id}, agent {thread.AgentId}]\n";
                var started = false;
                while (count < thread.DisplayMessages.Count)
                {
                    var line = Line(thread, count);
                    if (string.IsNullOrWhiteSpace(line) || offset >= line.Length)
                    {
                        count++;
                        offset = 0;
                        continue;
                    }

                    var prefix = evidence + (started ? "\n" : heading);
                    var remaining = line[offset..];
                    var length = FittingPrefix(prefix, remaining, budget, EstimateEvidence);
                    if (length == 0) { break; }

                    evidence.Append(started ? "\n" : heading).Append(remaining.AsSpan(0, length));
                    started = true;
                    offset += length;
                    if (offset < line.Length) { break; }

                    count++;
                    offset = 0;
                }

                if ((count, offset) != initial) { progress.Add((thread.Id, count, offset)); }
                // Keep the oldest incomplete source first on this and the next run.
                if (count < thread.DisplayMessages.Count) { break; }
            }

            if (evidence.Length == 0)
            {
                throw new InvalidOperationException("The model context has no room for pending conversation evidence. Increase the context allowance or shorten the task's other input.");
            }

            var instruction = Render(evidence.ToString());
            if (!instruction.Contains(evidence.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The task instruction must include its complete conversation evidence placeholder.");
            }

            return Task.FromResult(new AgentRunPlan(instruction, async successToken =>
            {
                foreach (var position in progress)
                {
                    await CommitAsync(scope, position.ThreadId, reflection, position.Count, position.Offset, successToken).ConfigureAwait(false);
                }
            }));
        });
    }

    // Split on text elements so emoji and combining characters survive consecutive batches.
    private static int FittingPrefix(string prefix, string text, int budget, Func<string, int> estimate)
    {
        if (estimate(prefix + text) <= budget) { return text.Length; }

        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var low = 0;
        var high = boundaries.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var length = middle == boundaries.Length ? text.Length : boundaries[middle];
            if (estimate(prefix + text[..length]) <= budget) { low = middle; }
            else { high = middle - 1; }
        }

        return low == boundaries.Length ? text.Length : boundaries[low];
    }

    private static string Line(ChatThread thread, int index)
        => ChatDisplayMessageFormatter.GetConversationLine(thread.DisplayMessages[index], includeNonTextSegments: false) ?? string.Empty;

    private static (int Count, int Offset) Position(ChatThread thread, bool reflection) => reflection
        ? (thread.LastReflectedMessageCount, thread.LastReflectedMessageOffset)
        : (thread.LastSummarizedMessageCount, thread.LastSummarizedMessageOffset);

    private static void SetPosition(ChatThread thread, bool reflection, int count, int offset)
    {
        if (reflection) { thread.LastReflectedMessageCount = count; thread.LastReflectedMessageOffset = offset; }
        else { thread.LastSummarizedMessageCount = count; thread.LastSummarizedMessageOffset = offset; }
    }

    private static Task CommitAsync(UserServiceScope scope, string threadId, bool reflection, int count, int offset, CancellationToken ct)
        => scope.ChatThreadRepository.UpdateWatermarksAsync(scope.UserId, threadId, null,
            reflection ? null : count, ct, reflection ? count : null,
            reflection ? null : offset, reflection ? offset : null);
}
