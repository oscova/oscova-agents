namespace Oscova.Agents.Core.Tasks;

/// <summary>
/// Reserved task folder keys used by seeded tasks and built-in task handlers.
/// </summary>
public static class WellKnownTasks
{
    /// <summary>
    /// Indexes unflushed conversation messages into the per-user vector store.
    /// </summary>
    public const string VectorIndexing = "vector-indexing";

    /// <summary>
    /// Saves noteworthy unsummarized conversation slices to today's daily log.
    /// </summary>
    public const string DailyMemory = "daily-memory";

    /// <summary>
    /// Seeds a normal scheduled task that reviews recent memory and conservatively promotes stable facts.
    /// </summary>
    public const string Dreaming = "dreaming";

    /// <summary>
    /// Reviews recent agent behavior and maintains private self-improvement notes.
    /// </summary>
    public const string Reflection = "reflection";

    /// <summary>
    /// Works the open backlog items the user delegated to the main agent.
    /// </summary>
    public const string NightShift = "night-shift";

    /// <summary>
    /// Reports overnight results and open backlog items in one dated brief thread.
    /// </summary>
    public const string MorningBrief = "morning-brief";

    /// <summary>Lists task folder keys reserved by Oscova Assistant product behavior.</summary>
    public static IReadOnlyList<string> ReservedFolderKeys { get; } =
    [
        VectorIndexing,
        DailyMemory,
        Dreaming,
        Reflection,
        NightShift,
        MorningBrief
    ];
}
