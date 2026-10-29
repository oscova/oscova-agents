namespace Oscova.Agents.Core.Nodes;

/// <summary>Identifies an offline node with a safe recovery message for project callers and chat viewers.</summary>
public sealed class NodeUnavailableException() : IOException("The selected node is offline. Start Oscova node on that device and try again.");
