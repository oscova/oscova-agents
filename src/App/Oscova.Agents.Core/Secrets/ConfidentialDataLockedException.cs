namespace Oscova.Agents.Core.Secrets;

/// <summary>Indicates that protected data cannot be written until the owning user unlocks it.</summary>
public sealed class ConfidentialDataLockedException(string message) : InvalidOperationException(message);
