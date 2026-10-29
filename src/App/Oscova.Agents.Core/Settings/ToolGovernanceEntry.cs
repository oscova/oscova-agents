using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Stores per-user policy for one qualified tool.
/// </summary>
public sealed record ToolGovernanceEntry
{
    public string QualifiedName { get; init; } = string.Empty;
    public NetworkScope? NetworkScopeOverride { get; init; }
    public ToolCallLimit? CallLimit { get; init; }
}

/// <summary>
/// Describes the fixed-window call budget applied to a qualified tool.
/// </summary>
public sealed record ToolCallLimit
{
    public const int MinCalls = 1;
    public const int MaxCallsAllowed = 1_000_000;
    public const int FixedWindowHours = 24;

    public bool IsEnabled { get; init; }
    public int MaxCalls { get; init; }
    public int WindowHours { get; init; } = FixedWindowHours;

    public bool IsValid()
    {
        return !IsEnabled ||
            (MaxCalls is >= MinCalls and <= MaxCallsAllowed &&
             WindowHours == FixedWindowHours);
    }
}
