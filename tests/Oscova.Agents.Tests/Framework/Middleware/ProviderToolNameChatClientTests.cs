using Microsoft.Extensions.AI;
using Oscova.Agents.Middleware;

namespace Oscova.Agents.Tests.Framework.Middleware;

public sealed class ProviderToolNameChatClientTests
{
    private const string ProviderSafePattern = "^[A-Za-z_][A-Za-z0-9_-]{0,63}$";

    /// <summary>A non-ASCII tool reaches the provider under a safe alias, and the provider's call comes back under the real name.</summary>
    [Fact]
    public async Task GetResponseAsync_WithNonAsciiTool_AliasesForProviderAndRestoresCall()
    {
        // Arrange: the provider calls whatever name it was offered.
        var arguments = new Dictionary<string, object?> { ["query"] = "menu" };
        using var transport = new TestChatClient
        {
            ResponseAsync = (_, options, _) => Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", options!.Tools![0].Name, arguments)])))
        };
        using var client = new ProviderToolNameChatClient(transport);

        // Act
        var response = await client.GetResponseAsync([new(ChatRole.User, "Search")], new ChatOptions { Tools = [Tool("café__search")] });

        // Assert: the provider saw only a safe alias; the caller sees the real name with the call id and arguments.
        var providerName = Assert.Single(transport.ResponseOptions[0]!.Tools!).Name;
        Assert.Matches(ProviderSafePattern, providerName);
        Assert.StartsWith("cafe__search_", providerName, StringComparison.Ordinal);
        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("café__search", call.Name);
        Assert.Equal("call-1", call.CallId);
        Assert.Same(arguments, call.Arguments);
    }

    /// <summary>Streamed calls to an alias come back under the real name.</summary>
    [Fact]
    public async Task GetStreamingResponseAsync_WithNonAsciiTool_RestoresStreamedCall()
    {
        // Arrange
        using var transport = new TestChatClient
        {
            StreamingAsync = (_, options, ct) => TestChatClient.StreamAsync(
                [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-1", options!.Tools![0].Name)])], ct)
        };
        using var client = new ProviderToolNameChatClient(transport);

        // Act
        var updates = await client.GetStreamingResponseAsync([new(ChatRole.User, "Search")], new ChatOptions { Tools = [Tool("日本語__search")] }).ToListAsync();

        // Assert
        var call = Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
        Assert.Equal("日本語__search", call.Name);
    }

    /// <summary>A call replayed from history reaches the provider under the same alias as the tool, without changing the caller's history.</summary>
    [Fact]
    public async Task GetResponseAsync_ReplayedHistoryCall_UsesToolAliasAndKeepsCallerHistory()
    {
        // Arrange: a previous turn called the tool and got its result.
        using var transport = new TestChatClient();
        using var client = new ProviderToolNameChatClient(transport);
        var previousCall = new FunctionCallContent("call-1", "café__search");
        List<ChatMessage> history =
        [
            new(ChatRole.User, "Search"),
            new(ChatRole.Assistant, [previousCall]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "found")]),
            new(ChatRole.User, "Again")
        ];

        // Act
        await client.GetResponseAsync(history, new ChatOptions { Tools = [Tool("café__search")] });

        // Assert: the provider's history call and tool declaration share one alias; the caller's message is untouched.
        var providerCall = Assert.Single(transport.ResponseMessages[0].SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal(transport.ResponseOptions[0]!.Tools![0].Name, providerCall.Name);
        Assert.Equal("call-1", providerCall.CallId);
        Assert.Same(previousCall, Assert.Single(history[1].Contents));
        Assert.Equal("café__search", previousCall.Name);
    }

    /// <summary>A history call the provider made under an unsafe name reaches it renamed, without the raw part an adapter would resend verbatim.</summary>
    [Fact]
    public async Task GetResponseAsync_ReplayedUnsafeCallWithRawPart_SendsOnlyTheAlias()
    {
        // Arrange: the model called a name it was never offered, and the adapter kept its own part for that call.
        using var transport = new TestChatClient();
        using var client = new ProviderToolNameChatClient(transport);
        var previousCall = new FunctionCallContent("call-1", "cafe__menu_du_café") { RawRepresentation = new object() };
        List<ChatMessage> history =
        [
            new(ChatRole.User, "Menu"),
            new(ChatRole.Assistant, [previousCall]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "Error: Requested function not found.")])
        ];

        // Act
        await client.GetResponseAsync(history, new ChatOptions { Tools = [Tool("café__menu_du_café")] });

        // Assert: the provider gets a safe name and no stale raw part; the caller's call keeps both.
        var providerCall = Assert.Single(transport.ResponseMessages[0].SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Matches(ProviderSafePattern, providerCall.Name);
        Assert.Null(providerCall.RawRepresentation);
        Assert.Equal("cafe__menu_du_café", previousCall.Name);
        Assert.NotNull(previousCall.RawRepresentation);
    }

    /// <summary>Names with the same ASCII fold, and a safe tool with that fold, each keep a distinct name and get their own calls back.</summary>
    [Fact]
    public async Task GetResponseAsync_NamesWithSameFold_GetDistinctAliasesAndMapBackSeparately()
    {
        // Arrange: the provider calls every tool it was offered.
        using var transport = new TestChatClient
        {
            ResponseAsync = (_, options, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [.. options!.Tools!.Select((tool, index) => new FunctionCallContent($"call-{index}", tool.Name))])))
        };
        using var client = new ProviderToolNameChatClient(transport);
        string[] names = ["café__x", "cafè__x", "cafe__x"];

        // Act
        var response = await client.GetResponseAsync([new(ChatRole.User, "Run")], new ChatOptions { Tools = [.. names.Select(Tool)] });

        // Assert
        var providerNames = transport.ResponseOptions[0]!.Tools!.Select(tool => tool.Name).ToList();
        Assert.Equal(3, providerNames.Distinct(StringComparer.Ordinal).Count());
        Assert.All(providerNames, name => Assert.Matches(ProviderSafePattern, name));
        Assert.Equal("cafe__x", providerNames[2]);
        Assert.Equal(names, response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.Name));
    }

    /// <summary>Requests with only provider-safe names pass through unchanged.</summary>
    [Fact]
    public async Task GetResponseAsync_WithSafeNamesOnly_PassesRequestThrough()
    {
        // Arrange
        using var transport = new TestChatClient();
        using var client = new ProviderToolNameChatClient(transport);
        var options = new ChatOptions { Tools = [Tool("web__fetch")], ToolMode = ChatToolMode.RequireSpecific("web__fetch") };
        ChatMessage[] history = [new(ChatRole.Assistant, [new FunctionCallContent("call-1", "web__fetch")])];

        // Act
        await client.GetResponseAsync(history, options);

        // Assert
        Assert.Same(options, transport.ResponseOptions[0]);
        Assert.Same(history[0], Assert.Single(transport.ResponseMessages[0]));
    }

    /// <summary>A required non-ASCII tool is required under its alias.</summary>
    [Fact]
    public async Task GetResponseAsync_RequiredNonAsciiTool_RequiresAlias()
    {
        // Arrange
        using var transport = new TestChatClient();
        using var client = new ProviderToolNameChatClient(transport);

        // Act
        await client.GetResponseAsync([new(ChatRole.User, "Run")],
            new ChatOptions { Tools = [Tool("café__x")], ToolMode = ChatToolMode.RequireSpecific("café__x") });

        // Assert
        var providerOptions = transport.ResponseOptions[0]!;
        var toolMode = Assert.IsType<RequiredChatToolMode>(providerOptions.ToolMode);
        Assert.Equal(providerOptions.Tools![0].Name, toolMode.RequiredFunctionName);
    }

    /// <summary>A provider-safe tool whose name equals another tool's alias fails the request instead of misrouting calls.</summary>
    [Fact]
    public async Task GetResponseAsync_SafeToolNamedLikeAlias_FailsWithoutCallingProvider()
    {
        // Arrange: learn the alias the provider is given for café__search.
        using var probe = new TestChatClient();
        using var probeClient = new ProviderToolNameChatClient(probe);
        await probeClient.GetResponseAsync([new(ChatRole.User, "Run")], new ChatOptions { Tools = [Tool("café__search")] });
        var alias = probe.ResponseOptions[0]!.Tools![0].Name;
        using var transport = new TestChatClient();
        using var client = new ProviderToolNameChatClient(transport);
        ChatMessage[] historyOnly = [new(ChatRole.Assistant, [new FunctionCallContent("call-1", "café__search")])];

        // Act and assert: both tools offered together, and the unsafe one only in history.
        var together = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new(ChatRole.User, "Run")], new ChatOptions { Tools = [Tool("café__search"), Tool(alias)] }));
        Assert.Contains("café__search", together.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync(historyOnly, new ChatOptions { Tools = [Tool(alias)] }));
        Assert.Equal(0, transport.ResponseCalls);
    }

    /// <summary>Two names whose aliases collide fail the request instead of misrouting calls.</summary>
    [Fact]
    public async Task GetResponseAsync_UnsafeNamesWithSameAlias_FailWithoutCallingProvider()
    {
        // Arrange: these two names share a fold and the first 8 hex characters of their hash.
        using var transport = new TestChatClient();
        using var client = new ProviderToolNameChatClient(transport);
        ChatMessage[] historyOnly = [new(ChatRole.Assistant, [new FunctionCallContent("call-1", "一桂__search")])];

        // Act and assert: both tools offered together, and one only in history.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new(ChatRole.User, "Run")], new ChatOptions { Tools = [Tool("一桂__search"), Tool("一狦__search")] }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync(historyOnly, new ChatOptions { Tools = [Tool("一狦__search")] }));
        Assert.Equal(0, transport.ResponseCalls);
    }

    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "done", name);
}
