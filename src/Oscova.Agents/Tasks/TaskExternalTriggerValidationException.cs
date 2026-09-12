using System;

namespace Oscova.Agents.Tasks;

/// <summary>Identifies why external task trigger parameter validation failed.</summary>
public enum TaskExternalTriggerValidationFailureKind
{
    /// <summary>The task does not allow external triggering.</summary>
    TriggerDisabled,

    /// <summary>A supplied parameter is not declared by the task.</summary>
    UnknownParameter,

    /// <summary>A supplied parameter does not match its declared JSON value kind.</summary>
    ParameterKindMismatch,

    /// <summary>A required declared parameter was not supplied.</summary>
    RequiredParameterMissing
}

/// <summary>Reports a typed external task trigger validation failure without transport wording.</summary>
public sealed class TaskExternalTriggerValidationException : InvalidOperationException
{
    internal TaskExternalTriggerValidationException(
        TaskExternalTriggerValidationFailureKind failureKind,
        string? parameterName = null,
        TaskExternalParameterKind? expectedKind = null)
        : base("External task trigger parameter validation failed.")
    {
        FailureKind = failureKind;
        ParameterName = parameterName;
        ExpectedKind = expectedKind;
    }

    /// <summary>Gets the validation failure category.</summary>
    public TaskExternalTriggerValidationFailureKind FailureKind { get; }

    /// <summary>Gets the affected declared or supplied parameter name, when applicable.</summary>
    public string? ParameterName { get; }

    /// <summary>Gets the expected parameter kind for a kind mismatch, when applicable.</summary>
    public TaskExternalParameterKind? ExpectedKind { get; }
}
