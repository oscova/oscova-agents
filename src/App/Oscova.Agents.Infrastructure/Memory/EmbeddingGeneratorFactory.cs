using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Llm;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Llm;
using Oscova.Agents.Policies;
using Oscova.Agents.Vector;
using Oscova.Agents.Infrastructure.Metrics;
using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Infrastructure.Memory
{
    /// <summary>Creates embedding clients using the current catalog and its credential owner.</summary>
    public class EmbeddingGeneratorFactory(
        ModelProviderSettings llmProvider,
        PrivacySettings privacySettings,
        ICredentialService credentialService,
        IModelProviderCatalog modelProviderCatalog,
        ExecutionPermissionRegistry executions,
        RunContextAccessor runs,
        ModelProviderActivity activity,
        ModelBudgetService budgets,
        IMetricsEventStore metrics,
        ILogger<EmbeddingGeneratorFactory> logger) : IEmbeddingGeneratorFactory
    {
        /// <summary>Resolves shared embedding credentials without changing personal credential ownership.</summary>
        public ICredentialService? OperatorCredentials { get; set; }
        /// <summary>Builds the opaque embedding-space identity shared by product vector indexes.</summary>
        internal static DurableVectorSpace? CreateVectorSpace(ModelProviderConfig? config)
        {
            if (config is null || string.IsNullOrWhiteSpace(config.ModelId) || config.VectorDimensions <= 0)
            {
                return null;
            }

            string fingerprint = string.Join(
                '\u001F',
                config.Id.ToString("D"),
                config.Type,
                string.IsNullOrWhiteSpace(config.BaseUrl) ? string.Empty : config.BaseUrl.Trim(),
                config.ModelId);
            return new DurableVectorSpace(fingerprint, config.VectorDimensions);
        }

        public async Task<EmbeddingClientResult> CreateAsync(
            bool requireLocalEndpoint = false,
            CancellationToken cancellationToken = default)
        {
            var config = llmProvider.SelectedEmbeddingModelProvider;
            if (config is null || string.IsNullOrWhiteSpace(config.ModelId))
            {
                return new(null);
            }

            var provider = modelProviderCatalog.Find(config.Type);
            if (provider?.SupportsEmbedding != true)
            {
                return new(null);
            }

            if ((privacySettings.IsLocalOnly || requireLocalEndpoint) &&
                EgressClassifier.Classify(
                    config.Type,
                    config.BaseUrl,
                    provider.DefaultEndpoint,
                    config.IsLocalEndpoint,
                    provider.DefaultIsLocalEndpoint) == EgressClass.Cloud)
            {
                throw new LocalOnlyEmbeddingProviderBlockedException(config.Id.ToString());
            }

            var version = executions.GetConfigurationVersion(ModelProviderExecution.Key(config.Id));
            await llmProvider.RequireAccessAsync(config.Id, cancellationToken).ConfigureAwait(false);
            if (runs.Current?.AdmittedEmbeddingProviderId != config.Id &&
                await budgets.EmbeddingPauseAsync(llmProvider.UserId, config.Id, cancellationToken).ConfigureAwait(false) is { } reason)
            {
                return new(null, reason);
            }

            var apiKey = await ResolveProviderCredentialAsync(config, cancellationToken).ConfigureAwait(false);
            var generator = await provider.CreateEmbeddingGeneratorAsync(config, apiKey, cancellationToken).ConfigureAwait(false);
            if (generator is null) { return new(null); }

            var governed = new ModelProviderExecution.EmbeddingGenerator(
                generator, config.Clone(), version, llmProvider, executions, runs, activity);
            return new(new MeteredEmbeddingGenerator(governed, config.Clone(), DescribeEgress()?.IsCloud == true,
                llmProvider.UserId, runs, metrics, budgets, logger));
        }

        public EmbeddingEgressInfo? DescribeEgress()
        {
            var config = llmProvider.SelectedEmbeddingModelProvider;
            if (config is null || string.IsNullOrWhiteSpace(config.ModelId))
            {
                return null;
            }

            var provider = modelProviderCatalog.Find(config.Type);
            if (provider?.SupportsEmbedding != true)
            {
                return null;
            }

            var isCloud = EgressClassifier.Classify(
                config.Type,
                config.BaseUrl,
                provider.DefaultEndpoint,
                config.IsLocalEndpoint,
                provider.DefaultIsLocalEndpoint) == EgressClass.Cloud;
            return new EmbeddingEgressInfo(isCloud, config.Id.ToString(), config.ModelId);
        }

        private async Task<string?> ResolveProviderCredentialAsync(
            ModelProviderConfig config,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.CredentialKey))
            {
                return null;
            }

            var ownerCredentials = llmProvider.OwnerOf(config.Id) == llmProvider ? credentialService : OperatorCredentials;
            var apiKey = ownerCredentials is null ? null : await ownerCredentials.ResolveFieldAsync(config.CredentialKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    $"Credential '{config.CredentialKey}' is not available for provider '{config.Id}'.");
            }

            return apiKey;
        }

        /// <summary>Records embedding usage once, including indexing outside a conversational run.</summary>
        private sealed class MeteredEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner,
            ModelProviderConfig config, bool cloud, string userId, RunContextAccessor runs,
            IMetricsEventStore metrics, ModelBudgetService budgets, ILogger logger) : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
        {
            public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            {
                var result = await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
                var usage = result.Usage;
                var context = runs.Current;
                var ledger = context?.UsageLedger ?? new UsageLedger();
                ledger.Add(context?.CurrentAgentId ?? string.Empty, config.Id, config.ModelId ?? string.Empty,
                    cloud ? config.InputCostPerMillionTokens : 0m, cloud ? config.OutputCostPerMillionTokens : 0m,
                    usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount);
                if (context?.UsageLedger is null)
                {
                    try
                    {
                        var metricsEvent = MetricsEventBuilder.Anonymous(ledger.Snapshot(), MetricsEventBuilder.BackgroundBucket);
                        await metrics.AppendAsync(userId, metricsEvent, CancellationToken.None).ConfigureAwait(false);
                        await budgets.NotifyCompletedAsync(userId, metricsEvent).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not save embedding usage for provider {ProviderId}.", config.Id);
                    }
                }

                return result;
            }
        }
    }
}
