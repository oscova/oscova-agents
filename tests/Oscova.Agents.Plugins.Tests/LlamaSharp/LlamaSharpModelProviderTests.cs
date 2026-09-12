using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Llm;
using Oscova.Agents.Plugins.LlamaSharp;

namespace Oscova.Agents.Plugins.Tests.LlamaSharp;

public sealed class LlamaSharpModelProviderTests : IDisposable
{
    private readonly string _modelsDirectory = Path.Combine(
        Path.GetTempPath(),
        "oscova-llamasharp-provider-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetAvailableModelsAsync_ReturnsSortedGgufFileNames()
    {
        Directory.CreateDirectory(_modelsDirectory);
        File.WriteAllText(Path.Combine(_modelsDirectory, "b-model.gguf"), string.Empty);
        File.WriteAllText(Path.Combine(_modelsDirectory, "ignored.txt"), string.Empty);
        File.WriteAllText(Path.Combine(_modelsDirectory, "A-model.gguf"), string.Empty);
        using var provider = CreateProvider();

        var models = await provider.GetAvailableModelsAsync(new ModelProviderConfig());

        Assert.Equal(["A-model.gguf", "b-model.gguf"], models.Select(model => model.Id));
    }

    [Fact]
    public async Task CreateClientAsync_MissingModel_ThrowsFileNotFoundException()
    {
        using var provider = CreateProvider();
        var config = new ModelProviderConfig { ModelId = "missing.gguf" };

        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.CreateClientAsync(config).AsTask());
    }

    [Fact]
    public async Task CreateClientAsync_TraversalPath_DoesNotEscapeModelDirectory()
    {
        using var provider = CreateProvider();
        var config = new ModelProviderConfig { ModelId = "../../outside.gguf" };

        var exception = await Record.ExceptionAsync(() => provider.CreateClientAsync(config).AsTask());

        var missing = Assert.IsType<FileNotFoundException>(exception);
        Assert.Contains(Path.GetFullPath(_modelsDirectory), missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateClientAsync_AbsolutePath_DoesNotEscapeModelDirectory()
    {
        using var provider = CreateProvider();
        var externalPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "outside.gguf"));
        var config = new ModelProviderConfig { ModelId = externalPath };

        var exception = await Record.ExceptionAsync(() => provider.CreateClientAsync(config).AsTask());

        var missing = Assert.IsType<FileNotFoundException>(exception);
        Assert.Contains(Path.GetFullPath(_modelsDirectory), missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_WithoutLoadedModels_IsSafe()
    {
        var provider = CreateProvider();

        var exception = Record.Exception(provider.Dispose);

        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_modelsDirectory))
        {
            Directory.Delete(_modelsDirectory, recursive: true);
        }
    }

    private LlamaSharpModelProvider CreateProvider() => new(
        NullLogger<LlamaSharpModelProvider>.Instance,
        _modelsDirectory);
}
