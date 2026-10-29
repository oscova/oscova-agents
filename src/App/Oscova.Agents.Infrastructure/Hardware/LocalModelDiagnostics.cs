using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Core.Llm;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Infrastructure.Hardware;

/// <summary>Admits installed plugin identities and maps isolated inference outcomes for existing Test callers.</summary>
public sealed class LocalModelDiagnostics(ModelProviderOwnership ownership, DiagnosticProcessSupervisor supervisor,
    IHostApplicationLifetime lifetime, ILogger<LocalModelDiagnostics> logger) : ILocalModelDiagnostics
{
    /// <inheritdoc />
    public async Task<ProviderTestResult> TestAsync(ModelProviderConfig config, CancellationToken cancellationToken)
    {
        var plugin = ownership.Find(config.Type);
        if (plugin is null) { return new() { Outcome = "skipped", Reason = "unsupported", TestKind = "inference", Message = "Isolated diagnostics are unavailable for this provider." }; }

        var sanitized = new ModelProviderConfig
        {
            Id = config.Id, Type = config.Type, ModelId = config.ModelId,
            ContextWindowSize = config.ContextWindowSize, Options = config.Clone().Options
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        var result = await supervisor.RunAsync(new("model", 0, 0, default,
            PluginId: plugin.Manifest.Id, PluginVersion: plugin.Manifest.Version, PluginDirectory: plugin.PluginDirectory,
            ModelDirectory: PathUtil.ModelsPath, Config: sanitized, ContributionIndex: ownership.ContributionIndex(config.Type)),
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), cancellation.Token).ConfigureAwait(false);
        var model = result.Response?.Model;
        if (model?.Outcome == "failed")
        {
            logger.LogWarning("Local model diagnostic failed for provider {ProviderId}, model {ModelId} in {ModelDirectory}: {Reason}. {Message}",
                config.Id, config.ModelId, PathUtil.ModelsPath, model.Reason, model.Message);
        }

        return new()
        {
            Outcome = model?.Outcome ?? result.Outcome,
            Reason = model?.Reason ?? result.Reason,
            Message = model?.Message ?? (result.Outcome switch
            {
                "skipped" => "Another local diagnostic is still running. Try again after it finishes.",
                "timedout" => "The local diagnostic exceeded its 60-second deadline.",
                "cancelled" => "The local diagnostic was cancelled.",
                _ => $"Local inference failed for {config.Name} ({config.ModelId}). Check the server log for the native cause. Reduce context or GPU layers, free GPU memory, or install the CPU edition and restart."
            }),
            TestKind = "inference", CleanupConfirmed = result.CleanupConfirmed,
            ElapsedMilliseconds = result.ElapsedMilliseconds, LoadMilliseconds = model?.LoadMilliseconds,
            InferenceMilliseconds = model?.InferenceMilliseconds, Runtime = model?.Runtime
        };
    }
}
