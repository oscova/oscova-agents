using System;

namespace Oscova.Agents.Skills;

/// <summary>
/// Signals that a skill import conflicts with an already installed skill directory.
/// </summary>
public sealed class SkillImportConflictException(string message) : Exception(message)
{
}
