using Oscova.Agents.Core.Localization;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>
/// Resolves user-facing text-channel elicitation strings in the ambient UI culture,
/// merging localized reply keywords with invariant fallbacks.
/// </summary>
internal sealed class TextChannelElicitationStrings(IElicitationTextLocalizer localizer)
{
    private static readonly char[] KeywordSeparators = [','];

    public IReadOnlyList<string> CancelKeywords
        => Merge(localizer.Localize(ElicitationTextKeys.CancelKeywords, "cancel"), "cancel");

    public IReadOnlyList<string> BooleanTrueKeywords
        => Merge(localizer.Localize(ElicitationTextKeys.BooleanTrueKeywords, "y,yes,true,1"), "y", "yes", "true", "1");

    public IReadOnlyList<string> BooleanFalseKeywords
        => Merge(localizer.Localize(ElicitationTextKeys.BooleanFalseKeywords, "n,no,false,0"), "n", "no", "false", "0");

    public string BooleanHint => localizer.Localize(ElicitationTextKeys.BooleanHint, "Reply yes or no.");
    public string StringHint => localizer.Localize(ElicitationTextKeys.StringHint, "Reply with text.");
    public string StringMinLengthHintFormat => localizer.Localize(ElicitationTextKeys.StringMinLengthHint, "Reply with text (at least {0} characters).");
    public string StringMaxLengthHintFormat => localizer.Localize(ElicitationTextKeys.StringMaxLengthHint, "Reply with text (at most {0} characters).");
    public string StringRangeHintFormat => localizer.Localize(ElicitationTextKeys.StringRangeHint, "Reply with text ({0}\u2013{1} characters).");
    public string NumberHint => localizer.Localize(ElicitationTextKeys.NumberHint, "Reply with a number.");
    public string IntegerHint => localizer.Localize(ElicitationTextKeys.IntegerHint, "Reply with a whole number.");
    public string EnumHintFormat => localizer.Localize(ElicitationTextKeys.EnumHint, "Reply with one of: {0}.");
    public string MultiFieldInstruction => localizer.Localize(ElicitationTextKeys.MultiFieldInstruction, "Reply with one value per line, in the order shown.");
    public string CancelInstruction => localizer.Localize(ElicitationTextKeys.CancelInstruction, "Reply `cancel` to abort.");
    public string Accepted => localizer.Localize(ElicitationTextKeys.Accepted, "Got it.");
    public string Cancelled => localizer.Localize(ElicitationTextKeys.Cancelled, "Cancelled.");
    public string CancelledTooManyAttempts => localizer.Localize(ElicitationTextKeys.CancelledTooManyAttempts, "Cancelled (too many invalid attempts).");
    public string InvalidBoolean => localizer.Localize(ElicitationTextKeys.InvalidBoolean, "Please reply with `yes` or `no`.");
    public string InvalidString => localizer.Localize(ElicitationTextKeys.InvalidString, "Reply cannot be empty.");
    public string InvalidStringTooShortFormat => localizer.Localize(ElicitationTextKeys.InvalidStringTooShort, "Reply must be at least {0} characters.");
    public string InvalidStringTooLongFormat => localizer.Localize(ElicitationTextKeys.InvalidStringTooLong, "Reply must be at most {0} characters.");
    public string InvalidNumber => localizer.Localize(ElicitationTextKeys.InvalidNumber, "Please reply with a number.");
    public string InvalidInteger => localizer.Localize(ElicitationTextKeys.InvalidInteger, "Please reply with a whole number.");
    public string InvalidNumberRangeFormat => localizer.Localize(ElicitationTextKeys.InvalidNumberRange, "Number must be between {0} and {1}.");
    public string InvalidEnumFormat => localizer.Localize(ElicitationTextKeys.InvalidEnum, "Please reply with one of: {0}.");
    public string InvalidLineCountFormat => localizer.Localize(ElicitationTextKeys.InvalidLineCount, "Expected {0} lines, got {1}.");

    private static IReadOnlyList<string> Merge(string localizedCsv, params string[] invariantKeywords)
    {
        var keywords = new HashSet<string>(invariantKeywords, StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in localizedCsv.Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            keywords.Add(keyword);
        }

        return [.. keywords];
    }
}
