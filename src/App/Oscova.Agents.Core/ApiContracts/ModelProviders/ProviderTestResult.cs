using Oscova.Agents.Llm;

namespace Oscova.Agents.Core.ApiContracts.ModelProviders;

/// <summary>Transient outcome of an explicit connectivity or isolated inference test.</summary>
public sealed class ProviderTestResult
{
    public bool Success => Outcome == "passed";
    public string Outcome { get; set; } = "failed";
    public string Reason { get; set; } = "connectivity";
    public string TestKind { get; set; } = "connectivity";
    public long ElapsedMilliseconds { get; set; }
    public long? LoadMilliseconds { get; set; }
    public long? InferenceMilliseconds { get; set; }
    public bool CleanupConfirmed { get; set; } = true;
    public ModelRuntimeEvidence? Runtime { get; set; }
    public string Message { get; set; } = string.Empty;
}
