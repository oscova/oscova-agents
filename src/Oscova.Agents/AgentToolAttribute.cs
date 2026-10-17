using System;

namespace Oscova.Agents;

/// <summary>Marks a public toolset method for exposure to an agent.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class AgentToolAttribute : Attribute;
