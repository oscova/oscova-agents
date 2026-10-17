namespace Oscova.Agents.Toolsets;

/// <summary>Saves oversized tool output where the current run's agent can read it back.</summary>
public interface IToolOutputStore
{
    /// <summary>Saves the content and returns its agent-facing path, or null when saving is unavailable.</summary>
    string? Save(string label, string content);
}
