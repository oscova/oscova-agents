namespace Oscova.Agents.Memory;

/// <summary>
/// Describes the outcome of removing a matching entry from indexed long-term memory.
/// </summary>
public sealed class MemoryForgetResult
{
    /// <summary>
    /// Gets whether any memory file changed.
    /// </summary>
    public bool Removed => FilesUpdated > 0;

    /// <summary>
    /// Gets or sets how many memory files were updated.
    /// </summary>
    public int FilesUpdated { get; set; }
}
