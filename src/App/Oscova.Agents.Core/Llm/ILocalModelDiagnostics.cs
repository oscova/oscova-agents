using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Core.Llm;

/// <summary>Runs admitted installed-provider diagnostics with host-owned process deadlines and cleanup.</summary>
public interface ILocalModelDiagnostics
{
    /// <summary>Checks one sanitized provider snapshot without accessing credentials or downloading assets.</summary>
    Task<ProviderTestResult> TestAsync(ModelProviderConfig config, CancellationToken cancellationToken);
}
