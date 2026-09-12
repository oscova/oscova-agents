using System;

namespace Oscova.Agents.Policies;

/// <summary>Marks reflected tools that are available only to non-confidential agents.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NonConfidentialAgentOnlyAttribute : Attribute;
