namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Identifies a saved question card and the user's answers.</summary>
public sealed record UserQuestionAnswer(string MessageId, string CallId,
    IReadOnlyDictionary<string, UserQuestionAnswer.Choice> Answers)
{
    /// <summary>Supplies one suggested option or a custom answer.</summary>
    public sealed record Choice(string? OptionId, string? Text);
}
