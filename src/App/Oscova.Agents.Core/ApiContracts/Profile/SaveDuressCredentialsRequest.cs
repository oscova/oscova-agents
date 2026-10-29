namespace Oscova.Agents.Core.ApiContracts.Profile;

/// <summary>Replaces both duress credentials and their shared deletion selection.</summary>
public sealed class SaveDuressCredentialsRequest
{
    public string AccountPassword { get; set; } = string.Empty;
    public string DuressPassword { get; set; } = string.Empty;
    public string ConfirmDuressPassword { get; set; } = string.Empty;
    public string? DuressPin { get; set; }
    public string? ConfirmDuressPin { get; set; }
    public bool DeleteAllThreads { get; set; }
    public IReadOnlyList<string> AgentIds { get; set; } = [];
    public IReadOnlyList<string> TaskIds { get; set; } = [];
    public IReadOnlyList<string> Workspaces { get; set; } = [];
    public bool ClearSharedFolder { get; set; }
}
