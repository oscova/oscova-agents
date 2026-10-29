namespace Oscova.Agents.Core.Personas;

/// <summary>
/// Built-in agent persona identifiers seeded as invariant system instructions during onboarding.
/// </summary>
public static class AgentPersonas
{
    /// <summary>
    /// No persona; leaves the system instruction untouched.
    /// </summary>
    public const string None = "none";

    /// <summary>
    /// Polished, structured, businesslike tone.
    /// </summary>
    public const string Professional = "professional";

    /// <summary>
    /// Warm, conversational, supportive tone.
    /// </summary>
    public const string Friendly = "friendly";

    /// <summary>
    /// Concise, minimal-preamble, direct tone.
    /// </summary>
    public const string Efficient = "efficient";

    /// <summary>
    /// The selectable persona ids in display order, excluding <see cref="None"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> Selectable = [Professional, Friendly, Efficient];

    /// <summary>
    /// Returns true when the id names a real seeded persona.
    /// </summary>
    public static bool IsSeeded(string? personaId) =>
        !string.IsNullOrWhiteSpace(personaId) && Selectable.Contains(personaId);
}
