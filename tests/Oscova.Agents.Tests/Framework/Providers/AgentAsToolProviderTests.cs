using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Providers;
using System.Reflection;
using System.Runtime.CompilerServices;

#pragma warning disable MAAI001

namespace Oscova.Agents.Tests.Framework.Providers;

public sealed class AgentAsToolProviderTests
{
    [Fact]
    public async Task InvokeAsync_WithSameConversationName_ResumesAfterParentSessionRoundTrip()
    {
        var chatClient = new RecordingChatClient();
        var child = CreateAgent("child", "Researcher", chatClient);
        var collection = new AIAgentCollection { child };
        var parentSession = await child.CreateSessionAsync();
        var provider = new AgentAsToolProvider([child.Id], collection);

        await InvokeAsync(provider, child, parentSession, "first question", "Research");
        var serializedParent = await child.SerializeSessionAsync(parentSession);
        parentSession = await child.DeserializeSessionAsync(serializedParent);
        provider = new AgentAsToolProvider([child.Id], collection);
        await InvokeAsync(provider, child, parentSession, "follow up", "research");

        Assert.Equal(2, chatClient.Calls.Count);
        Assert.Collection(
            chatClient.Calls[1].Where(message => message.Role != ChatRole.System),
            message => Assert.Equal("first question", message.Text),
            message => Assert.Equal("reply 1", message.Text),
            message => Assert.Equal("follow up", message.Text));

        var reader = new AgentAsToolProvider([], new AIAgentCollection());
        var conversation = Assert.Single(await reader.GetConversationsAsync(child, parentSession));
        Assert.Equal(child.Id, conversation.AgentId);
        Assert.Equal("Research", conversation.Name);
        Assert.Equal(
            ["first question", "reply 1", "follow up", "reply 2"],
            conversation.Messages.Where(message => message.Role != ChatRole.System).Select(message => message.Text));
    }

    [Fact]
    public async Task InvokeAsync_WithNamesDefaultsAndParents_KeepsHistoriesIsolated()
    {
        var chatClient = new RecordingChatClient();
        var child = CreateAgent("child", "Researcher", chatClient);
        var collection = new AIAgentCollection { child };
        var provider = new AgentAsToolProvider([child.Id], collection);
        var firstParent = await child.CreateSessionAsync();
        var secondParent = await child.CreateSessionAsync();

        await InvokeAsync(provider, child, firstParent, "alpha one", "alpha");
        await InvokeAsync(provider, child, firstParent, "beta one", "beta");
        await InvokeAsync(provider, child, firstParent, "default one");
        await InvokeAsync(provider, child, firstParent, "alpha two", "ALPHA");
        await InvokeAsync(provider, child, firstParent, "default two");
        await InvokeAsync(provider, child, secondParent, "alpha other", "alpha");

        Assert.DoesNotContain(chatClient.Calls[1], message => message.Text == "alpha one");
        Assert.DoesNotContain(chatClient.Calls[2], message => message.Text == "alpha one");
        Assert.Contains(chatClient.Calls[3], message => message.Text == "alpha one");
        Assert.Contains(chatClient.Calls[4], message => message.Text == "default one");
        Assert.DoesNotContain(chatClient.Calls[5], message => message.Text == "alpha one");

        var conversations = await provider.GetConversationsAsync(child, firstParent);
        Assert.Equal(3, conversations.Count);
        Assert.Contains(conversations, conversation => conversation.Name == "alpha");
        Assert.Contains(conversations, conversation => conversation.Name == "beta");
        Assert.Contains(conversations, conversation => conversation.Name is null);
    }

    [Fact]
    public async Task InvokeAsync_WhenCallFailsOrParentIsMissing_PreservesStoredStateAndUsesFreshSessions()
    {
        var chatClient = new RecordingChatClient();
        var child = CreateAgent("child", "Researcher", chatClient);
        var collection = new AIAgentCollection { child };
        var parentSession = await child.CreateSessionAsync();
        var provider = new AgentAsToolProvider([child.Id], collection);

        await InvokeAsync(provider, child, parentSession, "saved", "work");
        chatClient.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync(provider, child, parentSession, "failed", "work"));

        var conversation = Assert.Single(await provider.GetConversationsAsync(child, parentSession));
        Assert.Equal(["saved", "reply 1"], conversation.Messages.Select(message => message.Text));

        await InvokeAsync(provider, child, null, "stateless one");
        await InvokeAsync(provider, child, null, "stateless two");

        Assert.DoesNotContain(chatClient.Calls[^1], message => message.Text == "stateless one");
    }

    private static ChatClientAgent CreateAgent(string id, string name, IChatClient chatClient) =>
        new(chatClient, new ChatClientAgentOptions { Id = id, Name = name });

    private static async Task<object?> InvokeAsync(
        AgentAsToolProvider provider,
        AIAgent invokingAgent,
        AgentSession? parentSession,
        string query,
        string? conversation = null)
    {
        var method = typeof(AgentAsToolProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var invokingContext = new AIContextProvider.InvokingContext(invokingAgent, parentSession, new AIContext());
        var result = method.Invoke(provider, [invokingContext, CancellationToken.None]);
        var contextTask = Assert.IsType<ValueTask<AIContext>>(result);
        var context = await contextTask.ConfigureAwait(false);
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(context.Tools!));
        var arguments = new AIFunctionArguments { ["query"] = query };
        if (conversation is not null)
        {
            arguments["conversation"] = conversation;
        }

        return await function.InvokeAsync(arguments, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];
        public bool FailNext { get; set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = messages.ToList();
            Calls.Add(call);
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Child failed.");
            }

            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"reply {Calls.Count}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }
    }
}
