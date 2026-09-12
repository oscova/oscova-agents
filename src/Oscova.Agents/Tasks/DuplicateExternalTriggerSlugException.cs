using System;

namespace Oscova.Agents.Tasks;

/// <summary>Identifies an external-trigger slug already owned by another task.</summary>
public sealed class DuplicateExternalTriggerSlugException(string slug)
    : InvalidOperationException($"External trigger slug '{slug}' is already used by another task.");
