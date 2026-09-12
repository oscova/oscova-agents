using Microsoft.Extensions.AI;
using Oscova.Agents.Tools.Vision;

namespace Oscova.Agents.Tests.Framework.Providers;

public sealed class AnalyzeImageToolTests
{
    [Fact]
    public void AnalyzeImageTool_AsAITool_ExposesAnalyzeImageByName()
    {
        var tool = new AnalyzeImageTool(new StubChatClient()).AsAITool();
        Assert.Equal(AnalyzeImageTool.ToolName, tool.Name);
    }

    [Fact]
    public async Task AnalyzeAsync_NonAbsolutePath_ReturnsPathMustBeAbsolute()
    {
        var tool = new AnalyzeImageTool(new StubChatClient()).AsAITool();
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "relative/image.png",
            ["question"] = "what?"
        });
        Assert.Contains("must be absolute", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingFile_ReturnsNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"oscova-missing-{Guid.NewGuid():N}.png");
        var tool = new AnalyzeImageTool(new StubChatClient()).AsAITool();
        var result = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = missing,
            ["question"] = "what?"
        });
        Assert.Contains("not found", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_UnsupportedExtension_ReturnsUnsupported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oscova-{Guid.NewGuid():N}.bmp");
        await File.WriteAllBytesAsync(path, [0x01, 0x02]);
        try
        {
            var tool = new AnalyzeImageTool(new StubChatClient()).AsAITool();
            var result = await tool.InvokeAsync(new AIFunctionArguments
            {
                ["path"] = path,
                ["question"] = "what?"
            });
            Assert.Contains("unsupported", result?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private static class AsyncEnumerable
        {
            public static async IAsyncEnumerable<T> Empty<T>() { await Task.CompletedTask.ConfigureAwait(false); yield break; }
        }
    }
}
