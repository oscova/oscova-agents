namespace Oscova.Agents.Core.Auth;

/// <summary>Defines the numeric credentials accepted by sign-in and duress PIN entry.</summary>
public static class PinPolicy
{
    public const int MinimumLength = 6;
    public const int MaximumLength = 64;

    /// <summary>Accepts only bounded ASCII digits while preserving leading zeros.</summary>
    public static bool IsValid(string? pin) =>
        pin is { Length: >= MinimumLength and <= MaximumLength } &&
        pin.All(character => character is >= '0' and <= '9');
}
