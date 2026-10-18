using System.Collections.Concurrent;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Plugins.LlamaSharp;

/// <summary>Loads local GGUF chat models through LLamaSharp.</summary>
public sealed class LlamaSharpModelProvider : IModelProvider, IDisposable
{
    private readonly ILogger<LlamaSharpModelProvider> _logger;
    private readonly string _modelsDirectory;
    private readonly ConcurrentDictionary<string, ModelEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <summary>Creates a provider that reads GGUF files from the supplied model directory.</summary>
    public LlamaSharpModelProvider(
        ILogger<LlamaSharpModelProvider> logger,
        string modelsDirectory)
    {
        _logger = logger;
        _modelsDirectory = modelsDirectory;

        try
        {
            Directory.CreateDirectory(_modelsDirectory);
        }
        catch
        {
            // Access reports the actionable failure when the directory is first used.
        }
    }

    public string Type => ModelProviderKeys.Local;

    public string? DefaultEndpoint => null;

    public string? DefaultModel => null;

    public bool? DefaultIsLocalEndpoint => true;

    public bool SupportsEmbedding => false;

    public async ValueTask<IChatClient> CreateClientAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var modelPath = ResolveModelPath(config);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"GGUF model file not found: {modelPath}");
        }

        var entry = await GetOrLoadModelAsync(modelPath, config.ContextWindowSize, cancellationToken).ConfigureAwait(false);
        return entry.CreateChatClient();
    }

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Local GGUF models do not support embedding generation.");
    }

    public Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_modelsDirectory))
        {
            return Task.FromResult<IReadOnlyList<ModelDescriptor>>(Array.Empty<ModelDescriptor>());
        }

        // GGUF files carry no listable metadata; the user supplies context window and capabilities by hand.
        var descriptors = Directory.GetFiles(_modelsDirectory, "*.gguf")
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ModelDescriptor { Id = name })
            .ToList();

        return Task.FromResult<IReadOnlyList<ModelDescriptor>>(descriptors);
    }

    public Task<(bool Success, string Message)> TestAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ModelId))
        {
            return Task.FromResult((false, "No model file selected."));
        }

        var modelPath = ResolveModelPath(config);

        if (!File.Exists(modelPath))
        {
            return Task.FromResult((false, $"Model file not found: {modelPath}"));
        }

        return Task.FromResult((true, $"Model file exists: {Path.GetFileName(modelPath)}"));
    }

    public void Dispose()
    {
        foreach (var entry in _cache.Values)
        {
            entry.Dispose();
        }

        _cache.Clear();
        _loadGate.Dispose();
    }

    private async Task<ModelEntry> GetOrLoadModelAsync(string modelPath, int contextSize, CancellationToken ct)
    {
        var cacheKey = $"{modelPath}::{contextSize}";

        if (_cache.TryGetValue(cacheKey, out var existing))
        {
            return existing;
        }

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(cacheKey, out existing))
            {
                return existing;
            }

            _logger.LogInformation("Loading local GGUF model: {ModelPath}", modelPath);

            var parameters = new ModelParams(modelPath)
            {
                ContextSize = contextSize > 0 ? (uint)contextSize : 4096,
            };

            var model = LLamaWeights.LoadFromFile(parameters);
            var entry = new ModelEntry(model, parameters);
            _cache[cacheKey] = entry;

            _logger.LogInformation("Loaded local GGUF model: {ModelPath}", modelPath);
            return entry;
        }
        finally
        {
            // Each load owns exactly one permit, including when native loading fails.
            _loadGate.Release();
        }
    }

    private string ResolveModelPath(ModelProviderConfig config)
    {
        // Strip directory components so a crafted model id cannot escape the model directory.
        var fileName = Path.GetFileName(config.ModelId ?? string.Empty);
        var modelsDirectory = Path.GetFullPath(_modelsDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(modelsDirectory, fileName));

        if (!fullPath.StartsWith(modelsDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && fullPath != modelsDirectory)
        {
            throw new InvalidOperationException($"Invalid model path: '{config.ModelId}'.");
        }

        return fullPath;
    }

    private sealed class ModelEntry(LLamaWeights model, ModelParams parameters) : IDisposable
    {
        private readonly LLamaWeights _model = model;
        private readonly ModelParams _parameters = parameters;
        private readonly ConcurrentBag<LLamaContext> _contexts = [];

        public IChatClient CreateChatClient()
        {
            var context = _model.CreateContext(_parameters);
            _contexts.Add(context);
            return new InteractiveExecutor(context).AsChatClient();
        }

        public void Dispose()
        {
            foreach (var context in _contexts)
            {
                context.Dispose();
            }

            _model.Dispose();
        }
    }
}
