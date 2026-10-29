using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Harness;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Owns conversational question validation and display-only transcript state.</summary>
public static class UserQuestions
{
    public const string ToolsetName = "User Question";
    public const string ToolName = "ask";
    public const string QualifiedToolName = "user_question__ask";
    public const string StateKey = "oscova.userQuestion";
    public const string PostedReceipt =
        "Questions posted. This turn is complete. Submitted answers arrive in a later user message.";
    public const string PendingReminder =
        """
        Your posted questions are still open. The user can answer them at any time.
        - Do not ask them again or wait for the answers.
        - If you need an answer to finish, report what you have now and end your turn. Submitted answers arrive in a later user message.
        """;
    public const string DismissedNotice =
        """
        The user dismissed your posted questions.
        - Do not ask them again.
        - Continue without the answers.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Describes one clarification with optional suggested answers.</summary>
    public sealed record Question(string Id, string Header, string Text, IReadOnlyList<Option> Options);

    /// <summary>Describes a suggested answer and its optional recommendation.</summary>
    public sealed record Option(string Id, string Label, string? Description = null, bool Recommended = false);

    /// <summary>Stores only the disposition and reserved answer identity.</summary>
    public sealed record SavedState(bool Dismissed, string AnswerMessageId);

    /// <summary>Identifies one successfully posted question batch in a transcript.</summary>
    public sealed record Card(string MessageId, string CallId, IReadOnlyList<Question> Questions, SavedState? SavedState);

    /// <summary>Describes whether a persisted question can still be answered.</summary>
    public enum CardState { Unfinished, Pending, Answered, Dismissed, Obsolete }

    /// <summary>Rejects malformed or oversized question batches before posting.</summary>
    public static void Validate(IReadOnlyList<Question> questions)
    {
        if (questions is null || questions.Count is < 1 or > 4)
        {
            throw new ToolFailureException("Questions must be a non-null array containing 1-4 questions.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            if (question is null)
            {
                throw new ToolFailureException("Question entries must not be null.");
            }

            if (!Required(question.Id, 64) || !ids.Add(question.Id))
            {
                throw new ToolFailureException("Question IDs must be nonblank, at most 64 characters, and unique within the batch (case-sensitive).");
            }

            if (!Required(question.Header, 24))
            {
                throw new ToolFailureException($"Question '{question.Id}': header length is {question.Header?.Length ?? 0}.",
                    "Supply a nonblank header of at most 24 characters, including spaces.");
            }

            if (!Required(question.Text, 1000))
            {
                throw new ToolFailureException($"Question '{question.Id}': text must be nonblank and at most 1000 characters.");
            }

            if (question.Options is null || question.Options.Count is 1 or > 4)
            {
                throw new ToolFailureException($"Question '{question.Id}': options must be [] for free text only, or an array of 2-4 options.");
            }

            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            var recommended = 0;
            foreach (var option in question.Options)
            {
                if (option is null)
                {
                    throw new ToolFailureException($"Question '{question.Id}': option entries must not be null.");
                }

                if (!Required(option.Id, 64) || !optionIds.Add(option.Id))
                {
                    throw new ToolFailureException($"Question '{question.Id}': option IDs must be nonblank, at most 64 characters, and unique within this question (case-sensitive).");
                }

                if (!Required(option.Label, 100))
                {
                    throw new ToolFailureException($"Question '{question.Id}', option '{option.Id}': label must be nonblank and at most 100 characters.");
                }

                if (option.Description?.Length > 300)
                {
                    throw new ToolFailureException($"Question '{question.Id}', option '{option.Id}': description must be at most 300 characters.");
                }

                if (option.Recommended && ++recommended > 1)
                {
                    throw new ToolFailureException($"Question '{question.Id}': set Recommended=true on at most one option.");
                }
            }
        }
    }

    /// <summary>Reads a successful posted call from either live or rehydrated content.</summary>
    public static bool TryRead(ChatDisplayMessage message, ChatSegment segment, out Card? card)
    {
        card = null;
        var call = segment.FunctionCall;
        var result = segment.FunctionResult;
        if (message.From != MessageOwner.Bot || call?.Name != QualifiedToolName ||
            result?.CallId != call.CallId || segment.ExecutionState == ToolExecutionState.Failed ||
            call.Arguments is null || !call.Arguments.TryGetValue("questions", out var value)) return false;

        try
        {
            if (Decode<string>(result.Result) != PostedReceipt) return false;
            var questions = Decode<Question[]>(value);
            Validate(questions!);
            SavedState? state = null;
            if (call.AdditionalProperties?.TryGetValue(StateKey, out var metadata) == true)
            {
                try
                {
                    state = Decode<SavedState>(metadata);
                    if (string.IsNullOrWhiteSpace(state?.AnswerMessageId)) state = null;
                }
                catch (JsonException) { /* Malformed display metadata leaves the card inactive. */ }
            }

            card = new(message.MessageId, call.CallId, questions!, state);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ToolFailureException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Finds a recognized card by its exact message and call identities.</summary>
    public static Card? Find(ChatThread thread, string messageId, string callId)
    {
        var message = thread.DisplayMessages.FirstOrDefault(message => message.MessageId == messageId);
        var segment = message?.Segments.FirstOrDefault(segment => segment.FunctionCall?.CallId == callId);
        return message is not null && segment is not null && TryRead(message, segment, out var card) ? card : null;
    }

    /// <summary>Derives eligibility from the first ordinary user turn after the card.</summary>
    public static CardState GetState(ChatThread thread, Card card)
    {
        var message = thread.DisplayMessages.FirstOrDefault(message => message.MessageId == card.MessageId);
        if (message is null) return CardState.Obsolete;
        if (card.SavedState is null) return CardState.Unfinished;
        if (card.SavedState.Dismissed) return CardState.Dismissed;
        var next = thread.DisplayMessages.Skip(thread.DisplayMessages.IndexOf(message) + 1)
            .FirstOrDefault(candidate => candidate.From == MessageOwner.User && !candidate.IsInteractionResponse);
        return next is null ? CardState.Pending : next.MessageId == card.SavedState.AnswerMessageId
            ? CardState.Answered : CardState.Obsolete;
    }

    /// <summary>Copies a display message before changing metadata shared with model content.</summary>
    public static ChatDisplayMessage WithState(ChatDisplayMessage message, string callId, SavedState state)
    {
        var copy = Decode<ChatDisplayMessage>(message)!;
        var call = copy.Segments.First(segment => segment.FunctionCall?.CallId == callId).FunctionCall!;
        call.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        call.AdditionalProperties[StateKey] = state;
        return copy;
    }

    /// <summary>Makes every unfinished card in the message answerable, or returns the same message when none is unfinished.</summary>
    public static ChatDisplayMessage Finalize(ChatDisplayMessage message)
    {
        foreach (var segment in message.Segments.ToArray())
        {
            if (TryRead(message, segment, out var card) && card!.SavedState is null)
            {
                message = WithState(message, card.CallId, new(false, $"msg_{Guid.NewGuid():N}"));
            }
        }

        return message;
    }

    /// <summary>Validates the complete answer set and formats it using stored question text.</summary>
    public static bool TryFormatAnswers(Card card,
        IReadOnlyDictionary<string, UserQuestionAnswer.Choice> answers, out string text)
    {
        text = string.Empty;
        if (answers is null || answers.Count != card.Questions.Count) return false;
        var result = new StringBuilder("Your answers:");
        for (var index = 0; index < card.Questions.Count; index++)
        {
            var question = card.Questions[index];
            if (!answers.TryGetValue(question.Id, out var choice) || choice is null) return false;
            var hasOption = !string.IsNullOrWhiteSpace(choice.OptionId);
            var hasText = !string.IsNullOrWhiteSpace(choice.Text);
            if (hasOption == hasText || choice.Text?.Length > 4000) return false;
            var answer = hasOption
                ? question.Options.FirstOrDefault(option => option.Id == choice.OptionId)?.Label
                : choice.Text!.Trim();
            if (answer is null) return false;
            result.Append('\n').Append(index + 1).Append(". ").Append(question.Text).Append('\n').Append(answer);
        }

        text = result.ToString();
        return true;
    }

    /// <summary>Prevents ordinary messages from impersonating submitted answers.</summary>
    public static bool IsReservedAnswerMessageId(ChatThread thread, string messageId) =>
        thread.DisplayMessages.Any(message => message.Segments.Any(segment =>
            TryRead(message, segment, out var card) && card!.SavedState?.AnswerMessageId == messageId));

    private static bool Required(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit;

    // The round trip accepts typed live values and persisted JsonElement values without sharing mutable content.
    private static T? Decode<T>(object? value) => JsonSerializer.SerializeToElement(value, JsonOptions).Deserialize<T>(JsonOptions);
}
