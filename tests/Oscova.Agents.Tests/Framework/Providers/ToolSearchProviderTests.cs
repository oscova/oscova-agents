using System.Reflection;
using Microsoft.Agents.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Providers;
using Oscova.Agents.Tests.Framework.TestTools;

namespace Oscova.Agents.Tests.Framework.Providers;

public sealed class ToolSearchProviderTests
{
    [Fact]
    public async Task ProvideAIContextAsync_ReturnsOnlySearchToolBeforeUnlock()
    {
        var provider = CreateProvider(out _);

        var context = await InvokeProviderAsync(provider);

        var tool = Assert.Single(context.Tools!);
        Assert.Equal("search_tools", tool.Name);
        Assert.Contains("hidden behind `search_tools`", context.Instructions);
    }

    [Fact]
    public async Task ProvideAIContextAsync_ResurfacesUnlockedToolAfterSearch()
    {
        var provider = CreateProvider(out string playSongName);

        var searchResult = InvokeSearchTools(provider, "play song");
        var context = await InvokeProviderAsync(provider);

        Assert.Contains(playSongName, searchResult);
        Assert.Contains(context.Tools!, tool => tool.Name == "search_tools");
        Assert.Contains(context.Tools!, tool => tool.Name == playSongName);
    }

    [Fact]
    public async Task ProvideAIContextAsync_PinnedToolIsExposedAndExcludedFromSearch()
    {
        var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        var pinnedToolName = ToolNaming.BuildQualifiedName(qualifier, "PlaySong");
        var pinnedProvider = CreateProvider(out string playSongName, out string turnOnMusicName, pinnedToolName);

        var context = await InvokeProviderAsync(pinnedProvider);
        var searchResult = InvokeSearchTools(pinnedProvider, "play song");
        var unpinnedSearchResult = InvokeSearchTools(pinnedProvider, "embedded music player");

        Assert.Contains(context.Tools!, tool => tool.Name == "search_tools");
        Assert.Contains(context.Tools!, tool => tool.Name == playSongName);
        Assert.DoesNotContain(playSongName, searchResult);
        Assert.Contains(turnOnMusicName, unpinnedSearchResult);
    }

    private static ToolSearchProvider CreateProvider(out string playSongName, string? pinnedToolName = null)
    {
        return CreateProvider(out playSongName, out _, pinnedToolName);
    }

    private static ToolSearchProvider CreateProvider(
        out string playSongName,
        out string turnOnMusicName,
        string? pinnedToolName = null)
    {
        var collection = new CrewToolCollection
        {
            new SimpleTestTool()
        };
        var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
        var allowedToolNames = collection.Select(tool => tool.Name).ToList();
        playSongName = ToolNaming.BuildQualifiedName(qualifier, "PlaySong");
        turnOnMusicName = ToolNaming.BuildQualifiedName(qualifier, "TurnOnMusic");
        return new ToolSearchProvider(
            allowedToolNames,
            pinnedToolName is null ? [] : [pinnedToolName],
            collection,
            new AgentToolResolver(collection),
            new LexicalToolSearchStrategy());
    }

    private static string InvokeSearchTools(ToolSearchProvider provider, string query)
    {
        var method = typeof(ToolSearchProvider).GetMethod(
            "SearchTools",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(provider, [query]));
    }

    private static async Task<AIContext> InvokeProviderAsync(AIContextProvider provider)
    {
        var method = typeof(ToolSearchProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method.Invoke(provider, [null!, CancellationToken.None]);
        var contextTask = Assert.IsType<ValueTask<AIContext>>(result);
        return await contextTask.ConfigureAwait(false);
    }
}
