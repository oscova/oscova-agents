namespace Oscova.Agents.Core.Skills.Registry;

/// <summary>Reports measured preparation progress for one configured skill source.</summary>
public sealed record SkillRegistryPreparationProgress(
    SkillRegistryPreparationStage Stage,
    long ReceivedBytes = 0,
    int CompletedItems = 0,
    int TotalItems = 0)
{
    public double? Percent => TotalItems > 0 ? CompletedItems * 100d / TotalItems : null;
}

/// <summary>Identifies the current repository or API metadata preparation phase.</summary>
public enum SkillRegistryPreparationStage
{
    CheckingCache, Waiting, Connecting, Downloading, Indexing, PreparingFiles, Scanning, Cached, ReadingMetadata
}
