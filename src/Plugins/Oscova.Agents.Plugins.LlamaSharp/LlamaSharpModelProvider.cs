using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Plugins.LlamaSharp;

/// <summary>Loads local GGUF chat models through LLamaSharp.</summary>
public sealed class LlamaSharpModelProvider : IModelProvider, IModelProviderConfiguration, IModelHardwareProvider,
    ILocalModelDiagnosticProvider, IDisposable
{
    public const string GpuLayersOption = "gpuLayers";
    private readonly ILogger<LlamaSharpModelProvider> _logger;
    private readonly string _modelsDirectory;
    private readonly ConcurrentDictionary<Guid, ModelEntry> _cache = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly bool _cuda;
    private bool _disposed;

    /// <summary>Creates a provider that reads GGUF files from the supplied model directory.</summary>
    public LlamaSharpModelProvider(
        ILogger<LlamaSharpModelProvider> logger,
        string modelsDirectory, bool cuda = false)
    {
        _logger = logger;
        _modelsDirectory = modelsDirectory;
        _cuda = cuda;
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

        var errors = ValidateConfiguration(config);
        if (errors.Count > 0) { throw new ArgumentException(string.Join(" ", errors.Values), nameof(config)); }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var layers = GetGpuLayers(config);
            if (_cache.TryGetValue(config.Id, out var existing) && existing.Matches(modelPath, config.ContextWindowSize, layers))
            {
                return existing.CreateChatClient();
            }

            if (_cache.TryRemove(config.Id, out existing)) { existing.Dispose(); }
            if (_cuda && !HasCudaDevice()) { throw new InvalidOperationException("CUDA 13 initialization found no CUDA backend device. Install the CPU edition and restart, or correct the CUDA runtime and driver."); }

            var parameters = new ModelParams(modelPath)
            {
                ContextSize = config.ContextWindowSize > 0 ? (uint)config.ContextWindowSize : 4096,
                GpuLayerCount = layers
            };
            var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
            var entry = new ModelEntry(weights, parameters, config.ContextWindowSize);
            try
            {
                var client = entry.CreateChatClient();
                _cache[config.Id] = entry;
                return client;
            }
            catch
            {
                entry.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Local model {Model} could not initialize for provider {ProviderId}.", config.ModelId, config.Id);
            throw;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Checks requested layers without accessing a model or native runtime.</summary>
    public IReadOnlyDictionary<string, string> ValidateConfiguration(ModelProviderConfig config)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in config.Options ?? new Dictionary<string, string>())
        {
            if (pair.Key != GpuLayersOption || !int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var layers) || layers < 1)
            {
                errors[pair.Key] = "GPU layers must be an integer from 1 through 2147483647. Only gpuLayers is supported.";
            }
        }

        return errors;
    }

    /// <summary>Describes the edition and requested placement without initializing native code.</summary>
    public ModelHardwareProfile GetHardwareProfile(ModelProviderConfig config) => ValidateConfiguration(config).Count > 0
        ? new("unknown", "unknown") : _cuda
        ? new("cuda", config.Options?.ContainsKey(GpuLayersOption) == true ? "partial-gpu" : "full-gpu",
            config.Options?.ContainsKey(GpuLayersOption) == true ? GetGpuLayers(config) : null)
        : OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? new("metal", "unified") : new("cpu", "cpu");

    private int GetGpuLayers(ModelProviderConfig config) => _cuda
        ? config.Options?.TryGetValue(GpuLayersOption, out var value) == true ? int.Parse(value, CultureInfo.InvariantCulture) : -1
        : OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? -1 : 0;

    private static bool HasCudaDevice()
    {
        for (nuint index = 0; index < NativeApi.ggml_backend_dev_count(); index++)
        {
            var name = Marshal.PtrToStringUTF8(NativeApi.ggml_backend_dev_name(NativeApi.ggml_backend_dev_get(index)));
            if (name?.StartsWith("CUDA", StringComparison.Ordinal) == true) { return true; }
        }

        return false;
    }

    /// <summary>Releases one configuration after the host has drained its admitted work.</summary>
    public async Task ReleaseConfigurationAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryRemove(providerId, out var entry)) { entry.Dispose(); }
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>Proves a local decode and extracts only bounded, observed native placement evidence.</summary>
    public async Task<LocalModelDiagnosticResult> RunDiagnosticAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var availability = await TestAsync(config, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!availability.Success)
        {
            return new("failed", "model-unavailable", "Select an available GGUF model file, save the provider, and test again.", timer.ElapsedMilliseconds);
        }

        var nativeLog = new StringBuilder();
        var logGate = new Lock();
        NativeLogConfig.llama_log_set((_, message) =>
        {
            lock (logGate)
            {
                if (nativeLog.Length < 32 * 1024) { nativeLog.Append(message.AsSpan(0, Math.Min(message.Length, 32 * 1024 - nativeLog.Length))); }
            }
        });

        using var client = await CreateClientAsync(config, cancellationToken: cancellationToken).ConfigureAwait(false);
        var loaded = timer.ElapsedMilliseconds;
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")], new ChatOptions { MaxOutputTokens = 1 }, cancellationToken).ConfigureAwait(false);
        string observations;
        lock (logGate) { observations = nativeLog.ToString(); }

        var layersMatch = Regex.Match(observations, @"offloaded (?<layers>\d+)/\d+ layers to GPU", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
        var bufferMatch = Regex.Match(observations, @"(?<device>CUDA\d+)\s+model buffer size\s*=\s*(?<size>[0-9.]+)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
        var cudaProven = _cuda && layersMatch.Success && bufferMatch.Success &&
            int.TryParse(layersMatch.Groups["layers"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var layers) && layers > 0 &&
            double.TryParse(bufferMatch.Groups["size"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size) && size > 0;
        var evidence = cudaProven
            ? new ModelRuntimeEvidence("cuda", bufferMatch.Groups["device"].Value, int.Parse(layersMatch.Groups["layers"].Value, CultureInfo.InvariantCulture))
            : new ModelRuntimeEvidence(null, null, null);
        if (!_cuda)
        {
            var metal = Regex.IsMatch(observations, @"Metal\s+model buffer size\s*=\s*[1-9]", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
            var cpu = Regex.IsMatch(observations, @"CPU(?:_Mapped)?\s+model buffer size\s*=\s*[1-9]", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
            evidence = metal && layersMatch.Success ? new("metal", "Metal", int.Parse(layersMatch.Groups["layers"].Value, CultureInfo.InvariantCulture))
                : cpu && GetGpuLayers(config) == 0 ? new("cpu", "CPU", 0) : evidence;
        }

        return new("passed", "inference-completed", "Local model inference completed.", timer.ElapsedMilliseconds,
            loaded, timer.ElapsedMilliseconds - loaded, evidence);
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
        _loadGate.Wait();
        try
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (var entry in _cache.Values) { entry.Dispose(); }

            _cache.Clear();
        }
        finally { _loadGate.Release(); }
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

    private sealed class OutputBudgetExecutor(LLamaContext context) : InteractiveExecutor(context)
    {
        protected override async Task PreprocessInputs(string? text, InferStateArgs args, CancellationToken cancellationToken = default)
        {
            // LLamaSharp 0.30 subtracts follow-up prompt tokens from the requested output budget.
            var outputBudget = args.RemainedTokens;
            await base.PreprocessInputs(text, args, cancellationToken).ConfigureAwait(false);
            args.RemainedTokens = outputBudget;
        }
    }

    private sealed class ModelEntry(LLamaWeights model, ModelParams parameters, int contextSize) : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly HashSet<OwnedContextClient> _clients = [];
        private bool _retired;
        private bool _weightsDisposed;

        public bool Matches(string path, int context, int layers) => parameters.ModelPath == path && contextSize == context && parameters.GpuLayerCount == layers;

        public IChatClient CreateChatClient()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_retired, this);
                var context = model.CreateContext(parameters);
                try
                {
                    var client = new OwnedContextClient(new OutputBudgetExecutor(context).AsChatClient(), context, Remove);
                    _clients.Add(client);
                    return client;
                }
                catch { context.Dispose(); throw; }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_retired) { return; }
                _retired = true;
                foreach (var client in _clients.ToArray()) { client.Dispose(); }

                ReleaseWeights();
            }
        }

        private void Remove(OwnedContextClient client)
        {
            lock (_gate) { _clients.Remove(client); ReleaseWeights(); }
        }

        private void ReleaseWeights()
        {
            if (_retired && _clients.Count == 0 && !_weightsDisposed)
            {
                _weightsDisposed = true;
                model.Dispose();
            }
        }
    }

    /// <summary>Serializes native context use and delays destruction until an active call has finished.</summary>
    private sealed class OwnedContextClient(IChatClient inner, LLamaContext context, Action<OwnedContextClient> released)
        : DelegatingChatClient(inner)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile bool _retired;
        private bool _destroyed;

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_retired, this);
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            finally { FinishCall(); }
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_retired, this);
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false)) { yield return update; }
            }
            finally { FinishCall(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing) { return; }
            _retired = true;
            ReleaseIfIdle();
        }

        private void FinishCall()
        {
            _gate.Release();
            if (_retired) { ReleaseIfIdle(); }
        }

        private void ReleaseIfIdle()
        {
            if (!_gate.Wait(0)) { return; }

            try
            {
                if (_retired && !_destroyed)
                {
                    _destroyed = true;
                    context.Dispose();
                    released(this);
                }
            }
            finally { _gate.Release(); }
        }
    }
}
