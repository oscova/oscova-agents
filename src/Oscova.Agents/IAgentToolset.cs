namespace Oscova.Agents;

/// <summary>
/// Marks a trusted class as a group of tools that can be reflected into the agent runtime.
/// </summary>
/// <remarks>
/// Public methods on implementing types are discovered separately by the tool collection and converted into AI-callable functions.
/// </remarks>
public interface IAgentToolset;
