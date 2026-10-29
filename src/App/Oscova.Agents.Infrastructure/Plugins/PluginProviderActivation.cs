using Microsoft.Extensions.Logging;
using Oscova.Agents.Llm;
using Oscova.Agents.Plugins;
using Oscova.Agents.Speech;
using Oscova.Agents.Infrastructure.Speech;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Llm;

namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Admits plugin providers once and releases their instances when the Host stops.</summary>
internal sealed class PluginProviderActivation(
    IAgentPluginCatalog pluginCatalog,
    PluginContextFactory contexts,
    IEnumerable<IModelProvider> modelBuiltIns,
    IEnumerable<ISpeechRecognitionProvider> recognitionBuiltIns,
    IEnumerable<ISpeechSynthesisProvider> synthesisBuiltIns,
    ILogger<PluginProviderActivation> logger, SpeechProviderOwnership ownership,
    ModelProviderOwnership modelOwnership, ExecutionPermissionRegistry executions) : IAsyncDisposable
{
    private readonly PluginContributionLifetime _lifetime = new(logger);
    private readonly HashSet<object> _rejected = new(ReferenceEqualityComparer.Instance);
    public IReadOnlyList<IModelProvider> ModelProviders { get; private set; } = [];
    public IReadOnlyList<ISpeechRecognitionProvider> SpeechRecognitionProviders { get; private set; } = [];
    public IReadOnlyList<ISpeechSynthesisProvider> SpeechSynthesisProviders { get; private set; } = [];

    /// <summary>Admits provider instances before runtime catalogs are first resolved.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var models = new List<IModelProvider>();
        var recognition = new List<ISpeechRecognitionProvider>();
        var synthesis = new List<ISpeechSynthesisProvider>();
        var modelTypes = modelBuiltIns.Select(item => item.Type).ToHashSet(StringComparer.Ordinal);
        var recognitionTypes = recognitionBuiltIns.Select(item => item.Type).ToHashSet(StringComparer.Ordinal);
        var synthesisTypes = synthesisBuiltIns.Select(item => item.Type).ToHashSet(StringComparer.Ordinal);
        foreach (var plugin in pluginCatalog.Plugins)
        {
            ct.ThrowIfCancellationRequested();
            var context = contexts.Create(plugin, UnavailableCredentialResolver.Instance, TimeProvider.System);
            await ActivateAsync(plugin, plugin.ModelProviders, context, models, modelTypes,
                static item => item.Type, "model", (type, index) => modelOwnership.Add(type, plugin, index)).ConfigureAwait(false);
            await ActivateAsync(plugin, plugin.SpeechRecognitionProviders, context, recognition, recognitionTypes,
                static item => item.Type, "speech recognition", (type, _) => ownership.AddRecognition(type, plugin)).ConfigureAwait(false);
            await ActivateAsync(plugin, plugin.SpeechSynthesisProviders, context, synthesis, synthesisTypes,
                static item => item.Type, "speech synthesis", (type, _) => ownership.AddSynthesis(type, plugin)).ConfigureAwait(false);
        }

        ModelProviders = models.ToArray();
        SpeechRecognitionProviders = recognition.ToArray();
        SpeechSynthesisProviders = synthesis.ToArray();
        executions.ConfigurationChanged += ReleaseConfigurationAsync;
    }

    private async Task ActivateAsync<TProvider>(LoadedAgentPlugin plugin,
        IReadOnlyList<AgentProviderContribution<TProvider>> contributions, AgentPluginContext context,
        List<TProvider> accepted, HashSet<string> types, Func<TProvider, string> getType, string kind,
        Action<string, int>? recordOwner = null)
        where TProvider : class
    {
        for (var index = 0; index < contributions.Count; index++)
        {
            var contribution = contributions[index];
            TProvider? provider = null;
            string? type = null;
            try
            {
                provider = contribution.Factory(context)
                    ?? throw new InvalidOperationException("The provider factory returned null.");
                if (_rejected.Contains(provider))
                {
                    throw new InvalidOperationException("The provider instance was already rejected.");
                }

                type = getType(provider);
                if (string.IsNullOrWhiteSpace(type) || !types.Add(type))
                {
                    throw new InvalidOperationException($"Provider type '{type}' is empty or already registered.");
                }

                _lifetime.Add(provider);
                accepted.Add(provider);
                // Settings ownership follows successful runtime admission, including duplicate rejection.
                recordOwner?.Invoke(type, index);
            }
            catch (Exception ex)
            {
                if (provider is not null && !_lifetime.Contains(provider) && _rejected.Add(provider))
                {
                    var rejected = new PluginContributionLifetime(logger);
                    rejected.Add(provider);
                    await rejected.DisposeAsync().ConfigureAwait(false);
                }

                logger.LogError(ex, "Plugin {PluginId} {ProviderKind} provider {Type} with identity '{ProviderType}' could not be admitted.",
                    plugin.Manifest.Id, kind, contribution.ProviderType.FullName, type);
                pluginCatalog.ReportDiagnostic(new(AgentPluginLoadDiagnosticLevel.Warning, plugin.PluginDirectory,
                    $"{kind} provider '{contribution.ProviderType.FullName}' was rejected: {ex.Message}"));
            }
        }
    }

    /// <summary>Releases provider instances after runtime consumers stop.</summary>
    public ValueTask DisposeAsync()
    {
        executions.ConfigurationChanged -= ReleaseConfigurationAsync;
        return _lifetime.DisposeAsync();
    }

    private async Task ReleaseConfigurationAsync(string key)
    {
        var separator = key.LastIndexOf('/');
        if (separator < 0 || !Guid.TryParseExact(key[(separator + 1)..], "N", out var id) || key != ModelProviderExecution.Key(id)) { return; }

        foreach (var provider in ModelProviders.OfType<IModelProviderConfiguration>())
        {
            await provider.ReleaseConfigurationAsync(id).ConfigureAwait(false);
        }
    }

    private sealed class UnavailableCredentialResolver : IToolCredentialResolver
    {
        public static UnavailableCredentialResolver Instance { get; } = new();

        public Task<string?> ResolveAsync(string credentialKey, string fieldName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ResolveAsync(string credentialKey, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
}
