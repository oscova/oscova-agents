using System.ClientModel;
using System.Runtime.InteropServices;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Plugins.FoundryLocal;

/// <summary>Discovers, downloads, and runs local models through Microsoft Foundry Local.</summary>
public sealed class FoundryLocalModelProvider : IModelProvider, IModelDownloadProvider, IDisposable
{
    private static readonly HashSet<string> ChatTasks = new(StringComparer.Ordinal)
    {
        "chat-completion",
        "vision-language-chat"
    };

    private static nint _onnxRuntimeHandle;
    private static nint _onnxRuntimeGenAiHandle;
    private readonly ILogger<FoundryLocalModelProvider> _logger;
    private readonly string _storageDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FoundryLocalManager? _manager;

    /// <summary>Creates a provider whose application data and model cache live under the supplied directory.</summary>
    public FoundryLocalModelProvider(
        ILogger<FoundryLocalModelProvider> logger,
        string storageDirectory)
    {
        _logger = logger;
        _storageDirectory = storageDirectory;
    }

    public string Type => "FoundryLocal";

    public string? DefaultEndpoint => null;

    public string? DefaultModel => null;

    public bool? DefaultIsLocalEndpoint => true;

    public bool SupportsEmbedding => false;

    public async ValueTask<IChatClient> CreateClientAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ModelId))
        {
            throw new InvalidOperationException("Select a Foundry Local model before starting chat.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = await EnsureManagerAsync(cancellationToken).ConfigureAwait(false);
            var model = await GetModelAsync(manager, config.ModelId, cancellationToken).ConfigureAwait(false);
            if (!await model.IsCachedAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Foundry Local model '{config.ModelId}' is not downloaded. Download it from the provider settings first.");
            }

            await model.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (manager.Urls is null)
            {
                await manager.StartWebServiceAsync(cancellationToken).ConfigureAwait(false);
            }

            var serviceUrl = manager.Urls?.FirstOrDefault()
                ?? throw new InvalidOperationException("Foundry Local did not report a bound service URL.");
            var openAiClient = new OpenAIClient(
                new ApiKeyCredential("not-needed"),
                new OpenAIClientOptions { Endpoint = new Uri(serviceUrl.TrimEnd('/') + "/v1") });
            return openAiClient.GetChatClient(model.Id).AsIChatClient();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Foundry Local embeddings are not supported.");
    }

    public async Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = await EnsureManagerAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await manager.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
            var models = await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var cachedModels = await catalog.GetCachedModelsAsync(cancellationToken).ConfigureAwait(false);
            var cachedModelIds = cachedModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
            var descriptors = new List<ModelDescriptor>();

            foreach (var model in models.Where(model => ChatTasks.Contains(model.Info.Task ?? string.Empty)))
            {
                var contextLength = model.Info.ContextLength is > 0 and <= int.MaxValue
                    ? (int)model.Info.ContextLength.Value
                    : (int?)null;
                descriptors.Add(new ModelDescriptor
                {
                    Id = model.Alias,
                    ContextWindowSize = contextLength,
                    SupportsImages = model.Info.InputModalities?.Contains("image", StringComparison.OrdinalIgnoreCase),
                    IsInstalled = cachedModelIds.Contains(model.Id),
                    License = model.Info.License,
                    LicenseDescription = model.Info.LicenseDescription
                });
            }

            return descriptors.OrderBy(model => model.Id, StringComparer.Ordinal).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadModelAsync(
        string modelId,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("A Foundry Local model alias is required.", nameof(modelId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = await EnsureManagerAsync(cancellationToken).ConfigureAwait(false);
            var model = await GetModelAsync(manager, modelId, cancellationToken).ConfigureAwait(false);
            Action<float>? callback = progress is null ? null : value => progress.Report(value);
            await model.DownloadAsync(callback, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Success, string Message)> TestAsync(
        ModelProviderConfig config,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = await EnsureManagerAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await manager.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(config.ModelId))
            {
                var models = await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
                return models.Any(model => ChatTasks.Contains(model.Info.Task ?? string.Empty))
                    ? (true, "Connected to the Foundry Local catalog.")
                    : (false, "Foundry Local returned no chat-capable models.");
            }

            var model = await catalog.GetModelAsync(config.ModelId, cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                return (false, $"Foundry Local model alias '{config.ModelId}' was not found.");
            }

            return await model.IsCachedAsync(cancellationToken).ConfigureAwait(false)
                ? (true, $"Foundry Local model '{config.ModelId}' is ready.")
                : (false, $"Foundry Local model '{config.ModelId}' is not downloaded.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _manager?.Dispose();
        _gate.Dispose();
    }

    private async Task<FoundryLocalManager> EnsureManagerAsync(CancellationToken cancellationToken)
    {
        if (_manager is not null)
        {
            return _manager;
        }

        PreloadNativeDependencies();
        Directory.CreateDirectory(_storageDirectory);
        var modelCacheDirectory = Path.Combine(_storageDirectory, "models");
        Directory.CreateDirectory(modelCacheDirectory);
        var configuration = new Configuration
        {
            AppName = "Oscova",
            AppDataDir = _storageDirectory,
            ModelCacheDir = modelCacheDirectory,
            Web = new Configuration.WebService { Urls = "http://127.0.0.1:0" }
        };

        await FoundryLocalManager.CreateAsync(configuration, _logger, cancellationToken).ConfigureAwait(false);
        _manager = FoundryLocalManager.Instance;
        return _manager;
    }

    private static async Task<IModel> GetModelAsync(
        FoundryLocalManager manager,
        string modelId,
        CancellationToken cancellationToken)
    {
        var catalog = await manager.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        return await catalog.GetModelAsync(modelId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Foundry Local model alias '{modelId}' was not found.");
    }

    private static void PreloadNativeDependencies()
    {
        if (_onnxRuntimeHandle != 0 && _onnxRuntimeGenAiHandle != 0)
        {
            return;
        }

        // Native-to-native dependencies bypass AssemblyLoadContext, so load them in dependency order.
        var assembly = typeof(FoundryLocalManager).Assembly;
        var onnxRuntime = NativeLibrary.Load("onnxruntime", assembly, null);
        try
        {
            var onnxRuntimeGenAi = NativeLibrary.Load("onnxruntime-genai", assembly, null);
            _onnxRuntimeHandle = onnxRuntime;
            _onnxRuntimeGenAiHandle = onnxRuntimeGenAi;
        }
        catch
        {
            NativeLibrary.Free(onnxRuntime);
            throw;
        }
    }
}
