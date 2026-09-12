namespace Oscova.Agents.Runtime;

/// <summary>
/// Minimal seam for clearing cached agents after runtime state changes that should affect the next agent build.
/// </summary>
public interface IAgentCacheInvalidator
{
    /// <summary>
    /// Clears cached agents while preserving the synchronized tool collection.
    /// </summary>
    void InvalidateAllAgents();
}
