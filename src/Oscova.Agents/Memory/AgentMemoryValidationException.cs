using System;

namespace Oscova.Agents.Memory;

/// <summary>Reports an agent-readable memory validation failure.</summary>
/// <remarks>The message is authored for display and must not contain runtime or host details.</remarks>
public sealed class AgentMemoryValidationException : InvalidOperationException
{
    internal AgentMemoryValidationException(string message)
        : base(message)
    {
    }
}
