using System;

namespace Oscova.Agents.Skills;

/// <summary>
/// Carries trusted runtime attribution for skill markdown authored by an agent.
/// </summary>
public sealed record AgentAuthoredSkillContext(
    string AgentId,
    string AgentName,
    DateTimeOffset TimestampUtc);
