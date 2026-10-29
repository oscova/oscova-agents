namespace Oscova.Agents.Core.ApiContracts.HostControl;

/// <summary>
/// Lightweight anonymous host version response used by CLI readiness checks.
/// </summary>
public sealed class HostVersionResponse
{
    public string Version { get; set; } = string.Empty;
    public string FrameworkVersion { get; set; } = string.Empty;
    public string ApiCompatibility { get; set; } = "1";
    public string? ToolPackageVersion { get; set; }
}

/// <summary>
/// Authenticated host status details returned to CLI operators.
/// </summary>
public sealed class HostStatusResponse
{
    public int Pid { get; set; }
    public IReadOnlyList<string> Urls { get; set; } = [];
    public DateTimeOffset StartedAtUtc { get; set; }
    public string CurrentUserName { get; set; } = string.Empty;
    public bool RestartRequired { get; set; }
}
