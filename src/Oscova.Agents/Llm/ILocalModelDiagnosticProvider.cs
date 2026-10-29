using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Llm;

/// <summary>Opts into an explicitly requested inference check in an isolated process.</summary>
public interface ILocalModelDiagnosticProvider
{
    /// <summary>Loads the selected installed model and decodes at most one output token without downloads.</summary>
    Task<LocalModelDiagnosticResult> RunDiagnosticAsync(ModelProviderConfig config, CancellationToken cancellationToken = default);
}

/// <summary>Reports transient inference results and observed runtime evidence.</summary>
public sealed record LocalModelDiagnosticResult(string Outcome, string Reason, string Message,
    long ElapsedMilliseconds = 0, long? LoadMilliseconds = null, long? InferenceMilliseconds = null,
    ModelRuntimeEvidence? Runtime = null);

/// <summary>Contains observed execution facts, never requested placement presented as successful acceleration.</summary>
public sealed record ModelRuntimeEvidence(string? Backend, string? Device, int? OffloadedLayers);
