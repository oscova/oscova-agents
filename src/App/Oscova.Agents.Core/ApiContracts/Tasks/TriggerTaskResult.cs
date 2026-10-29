namespace Oscova.Agents.Core.ApiContracts.Tasks;

/// <summary>Result of manually triggering a scheduled task.</summary>
public sealed class TriggerTaskResult
{
    public bool Started { get; set; }
    public string? RunId { get; set; }
    public string Message { get; set; } = string.Empty;
}
