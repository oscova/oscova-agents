using Microsoft.Extensions.AI;
using Oscova.Agents.Llm;
using Oscova.Agents.Templating;
using Oscova.Agents.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Reducers;

/// <summary>
/// Compacts conversation history by stripping older tool chatter and summarizing older turns.
/// </summary>
/// <remarks>
/// Custom reducer kept instead of Microsoft.Extensions.AI's experimental SummarizingChatReducer:
/// that reducer triggers on message count only; this one budgets by tokens derived from the
/// model's context window, exposes the pre-compaction flush hook, and renders a user-editable
/// Scriban summarization prompt.
/// </remarks>
/// <param name="chatClient">The chat client used to generate the summary.</param>
/// <param name="maxTokenCount">The nominal token budget before compaction is considered.</param>
/// <param name="compactionThreshold">The fraction of the token budget that triggers summarization.</param>
public class SummarizingChatReducer(
    IChatClient chatClient,
    int maxTokenCount = SummarizingChatReducer.DefaultMaxTokenCount,
    double compactionThreshold = 0.8) : IChatReducer
{
    /// <summary>
    /// Historical default history budget used when the provider context window is unknown.
    /// </summary>
    public const int DefaultMaxTokenCount = ContextWindowBudget.DefaultHistoryBudget;

    /// <summary>
    /// Lowest derived history budget allowed for small-context models.
    /// </summary>
    public const int MinimumHistoryBudget = ContextWindowBudget.MinimumHistoryBudget;

    /// <summary>
    /// Default Scriban prompt used to ask the model for a compact conversation summary.
    /// </summary>
    public const string DefaultSummarizationPrompt = """
        This summary will replace the conversation in the agent's context window.
        Write it so the agent can continue correctly without the original messages.

        Capture in this priority order:
        1. User-declared facts and values - codes, identifiers, names, numbers, dates - verbatim, never paraphrased or abbreviated.
        2. Pending user requests not yet fulfilled (e.g. "I will ask you to do X later").
        3. Key decisions and conclusions from the discussion.

        Be brief on explanations. Be complete on facts.

        {{ chat_history }}

        Reminder: Begin the summary with any user-declared facts or values (codes, identifiers, names, numbers, dates) from the conversation above, written verbatim.
        """;

    /// <summary>
    /// Gets or sets the Scriban prompt template used for summarization.
    /// </summary>
    public string SummarizationPrompt { get; set; } = DefaultSummarizationPrompt;

    /// <summary>
    /// Gets the token budget this reducer compacts toward.
    /// </summary>
    public int MaxTokenCount => maxTokenCount;

    /// <summary>
    /// Derives the history token budget from a model's context window, reserving room for model
    /// output and for prompt content the reducer never sees (instructions, tools, memory).
    /// Unknown window sizes keep the historical default.
    /// </summary>
    public static int DeriveHistoryBudget(int contextWindowSize, int? maxOutputTokens)
    {
        return ContextWindowBudget.DeriveHistoryBudget(contextWindowSize, maxOutputTokens);
    }

    /// <summary>
    /// Async callback invoked before compaction begins. Awaited before any history is stripped or summarized.
    /// Use this to flush important context (e.g., to daily logs) before the chat history is compacted.
    /// </summary>
    public Func<CancellationToken, Task>? OnBeforeCompaction { get; set; }

    /// <summary>
    /// Invoked when compaction begins.
    /// </summary>
    public Action? OnCompactionStarted { get; set; }

    /// <summary>
    /// Invoked when compaction completes (or fails).
    /// </summary>
    public Action? OnCompactionCompleted { get; set; }

    /// <inheritdoc />
    public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        return ReduceCoreAsync(messages, force: false, cancellationToken);
    }

    /// <summary>
    /// Compacts older conversation history even when the automatic threshold has not been reached.
    /// </summary>
    public Task<IEnumerable<ChatMessage>> CompactAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        return ReduceCoreAsync(messages, force: true, cancellationToken);
    }

    private async Task<IEnumerable<ChatMessage>> ReduceCoreAsync(
        IEnumerable<ChatMessage> messages,
        bool force,
        CancellationToken cancellationToken)
    {
        var messageList = messages.ToList();

        // Separate system messages; they define agent behavior and must never be summarized or discarded.
        var systemMessages = messageList.Where(static m => m.Role == ChatRole.System).ToList();
        var conversationMessages = messageList.Where(static m => m.Role != ChatRole.System).ToList();

        if (!force && !ShouldCompact(conversationMessages))
        {
            return messageList;
        }

        var tailStart = force ? FindTailStart(conversationMessages, userTurnsToKeep: 2) : -1;
        if (force && tailStart == 0)
        {
            return messageList;
        }

        // Flush important context before any destructive compaction stage runs.
        if (OnBeforeCompaction is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await OnBeforeCompaction(cancellationToken).ConfigureAwait(false);
        }

        OnCompactionStarted?.Invoke();
        try
        {
            if (!force)
            {
                tailStart = FindTailStart(conversationMessages, userTurnsToKeep: 2);
            }

            var strippedMessages = StripToolRoundTrips(conversationMessages, tailStart, out tailStart);
            if (!force && FitsBudget(strippedMessages))
            {
                return [.. systemMessages, .. strippedMessages];
            }

            var summarized = await SummarizeWithTailAsync(systemMessages, strippedMessages, tailStart, cancellationToken).ConfigureAwait(false);
            if (FitsBudget(summarized))
            {
                return summarized;
            }

            var lastTurnTailStart = FindTailStart(strippedMessages, userTurnsToKeep: 1);
            if (lastTurnTailStart != tailStart)
            {
                summarized = await SummarizeWithTailAsync(systemMessages, strippedMessages, lastTurnTailStart, cancellationToken).ConfigureAwait(false);
                if (FitsBudget(summarized))
                {
                    return summarized;
                }
            }

            // Guaranteed progress: when even the recent tail is too large, summarize the whole conversation.
            return await SummarizeWithTailAsync(systemMessages, strippedMessages, strippedMessages.Count, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            OnCompactionCompleted?.Invoke();
        }
    }

    private bool ShouldCompact(IEnumerable<ChatMessage> messages)
    {
        return CountTokens(messages) >= maxTokenCount * compactionThreshold;
    }

    private bool FitsBudget(IEnumerable<ChatMessage> messages)
    {
        return CountTokens(messages.Where(static message => message.Role != ChatRole.System)) <= maxTokenCount;
    }

    private static int CountTokens(IEnumerable<ChatMessage> messages)
    {
        return AgentTokenCounter.CountTokens(FormatMessages(messages));
    }

    private async Task<IReadOnlyList<ChatMessage>> SummarizeWithTailAsync(
        IReadOnlyList<ChatMessage> systemMessages,
        IReadOnlyList<ChatMessage> conversationMessages,
        int tailStart,
        CancellationToken cancellationToken)
    {
        var middle = conversationMessages.Take(tailStart).ToList();
        var tail = conversationMessages.Skip(tailStart).ToList();
        if (middle.Count == 0)
        {
            return [.. systemMessages, .. tail];
        }

        var summaryMessage = await CreateSummaryMessageAsync(middle, cancellationToken).ConfigureAwait(false);
        return [.. systemMessages, summaryMessage, .. tail];
    }

    private async Task<ChatMessage> CreateSummaryMessageAsync(
        IEnumerable<ChatMessage> messagesToSummarize,
        CancellationToken cancellationToken)
    {
        var formattedHistory = FormatMessages(messagesToSummarize);
        var rendered = PromptTemplateRenderer.Render(SummarizationPrompt, new { chat_history = formattedHistory });
        var prompt = rendered.Contains(formattedHistory, StringComparison.Ordinal)
            ? rendered
            : $"{rendered}\n\n{formattedHistory}";
        var chatOptions = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = false } };
        var response = await chatClient.GetResponseAsync(prompt, chatOptions, cancellationToken).ConfigureAwait(false);

        // Summary uses ChatRole.Assistant: it is the assistant's recollection, not a directive.
        return new ChatMessage(ChatRole.Assistant, $"[Conversation Summary]\n{response.Text}");
    }

    private static int FindTailStart(IReadOnlyList<ChatMessage> messages, int userTurnsToKeep)
    {
        var usersSeen = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != ChatRole.User)
            {
                continue;
            }

            usersSeen++;
            if (usersSeen == userTurnsToKeep)
            {
                return MoveBoundaryBeforeToolSequence(messages, i);
            }
        }

        if (usersSeen > 0)
        {
            var firstUserIndex = messages
                .Select(static (message, index) => (message, index))
                .First(static item => item.message.Role == ChatRole.User)
                .index;
            return MoveBoundaryBeforeToolSequence(messages, firstUserIndex);
        }

        return messages.Count;
    }

    private static int MoveBoundaryBeforeToolSequence(IReadOnlyList<ChatMessage> messages, int boundary)
    {
        while (boundary > 0 && IsToolRelatedMessage(messages[boundary - 1]))
        {
            boundary--;
        }

        return boundary;
    }

    private static List<ChatMessage> StripToolRoundTrips(
        IReadOnlyList<ChatMessage> messages,
        int tailStart,
        out int newTailStart)
    {
        var result = new List<ChatMessage>(messages.Count);
        var omittedToolNames = new List<string>();

        for (var i = 0; i < tailStart; i++)
        {
            var message = messages[i];
            if (message.Role == ChatRole.Tool)
            {
                CollectFunctionNames(message, omittedToolNames);
                continue;
            }

            if (message.Role == ChatRole.Assistant &&
                message.Contents.Any(static content => content is FunctionCallContent))
            {
                var keptContents = new List<AIContent>();
                foreach (var content in message.Contents)
                {
                    if (content is FunctionCallContent functionCall)
                    {
                        omittedToolNames.Add(functionCall.Name);
                        continue;
                    }

                    keptContents.Add(content);
                }

                if (keptContents.Count > 0)
                {
                    result.Add(CloneWithContents(message, keptContents));
                }

                continue;
            }

            FlushToolTrace(result, omittedToolNames);
            result.Add(message);
        }

        FlushToolTrace(result, omittedToolNames);
        newTailStart = result.Count;
        for (var i = tailStart; i < messages.Count; i++)
        {
            result.Add(messages[i]);
        }

        return result;
    }

    private static void CollectFunctionNames(ChatMessage message, List<string> omittedToolNames)
    {
        foreach (var functionCall in message.Contents.OfType<FunctionCallContent>())
        {
            omittedToolNames.Add(functionCall.Name);
        }
    }

    private static void FlushToolTrace(List<ChatMessage> messages, List<string> omittedToolNames)
    {
        if (omittedToolNames.Count == 0)
        {
            return;
        }

        var names = omittedToolNames
            .GroupBy(static name => name, StringComparer.Ordinal)
            .Select(static group => group.Count() == 1 ? group.Key : $"{group.Key} x{group.Count()}");
        messages.Add(new ChatMessage(ChatRole.Assistant, $"[Tool calls omitted: {string.Join(", ", names)}]"));
        omittedToolNames.Clear();
    }

    private static ChatMessage CloneWithContents(ChatMessage message, IList<AIContent> contents)
    {
        return new ChatMessage(message.Role, contents)
        {
            AdditionalProperties = message.AdditionalProperties,
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            MessageId = message.MessageId,
            RawRepresentation = message.RawRepresentation
        };
    }

    private static string FormatMessages(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                sb.AppendLine($"{message.Role}: {message.Text}");
            }

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent functionCall:
                        sb.AppendLine($"{message.Role} tool call: {functionCall.Name}({SerializeArguments(functionCall.Arguments)})");
                        break;
                    case FunctionResultContent functionResult:
                        sb.AppendLine($"{message.Role} tool result: {AgentJsonText.SerializeValue(functionResult.Result)}");
                        break;
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsToolRelatedMessage(ChatMessage message)
    {
        return message.Role == ChatRole.Tool || message.Contents.Any(IsToolRelatedContent);
    }

    private static bool IsToolRelatedContent(AIContent content)
    {
        return content is FunctionCallContent
            or FunctionResultContent
            or InputRequestContent
            or InputResponseContent;
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        var ordered = arguments
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        return AgentJsonText.SerializeValue(ordered);
    }
}
