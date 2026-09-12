using System;

namespace Oscova.Agents.Credentials;

/// <summary>
/// Declares a single-field API-key credential slot for toolsets and providers.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CredentialSlotAttribute : Attribute
{
    /// <summary>Stable internal credential key (e.g. "openai").</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>User-facing slot label (e.g. "OpenAI API Key").</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>One-line description shown under the slot.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Optional environment-variable name pre-filled in the editor.</summary>
    public string? EnvVariable { get; init; }
}
