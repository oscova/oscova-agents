using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Harness;
using Oscova.Agents.Providers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
        var provider = new RecordingProvider(child.Id, collection);

        await InvokeAsync(provider, child, parentSession, "saved", "work");
        chatClient.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync(provider, child, parentSession, "failed", "work"));

        var conversation = Assert.Single(await provider.GetConversationsAsync(child, parentSession));
        Assert.Equal(["saved", "reply 1"], conversation.Messages.Select(message => message.Text));

        await InvokeAsync(provider, child, null, "stateless one");
        await InvokeAsync(provider, child, null, "stateless two");

        Assert.DoesNotContain(chatClient.Calls[^1], message => message.Text == "stateless one");
        Assert.Equal(4, provider.Sessions.Count);
        Assert.NotSame(provider.Sessions[^2], provider.Sessions[^1]);
    }

    [Fact]
    public async Task StartSubagent_SharesFourSlotsAndReleasesOneOnlyAfterItsWorkerExits()
    {
        var chatClient = new HeldChatClient();
        await using var work = new DelegatedWorkState(CancellationToken.None);
        var (parent, parentSession, provider) = await CreateSelfDelegationAsync(chatClient, work);
        var tools = await GetToolsAsync(provider, parent, parentSession);

        var handles = new List<string>();
        for (var index = 1; index <= DelegatedWorkState.MaxActiveWorkers; index++)
        {
            handles.Add(await StartAsync(tools, $"task {index}"));
            await chatClient.EnteredAsync($"task {index}");
        }

        var busy = await Assert.ThrowsAsync<ToolFailureException>(() => StartAsync(tools, "task 5"));
        Assert.Contains("All 4 subagent slots are busy", busy.Message, StringComparison.Ordinal);

        // A worker inherits the definition, including this provider, but is never offered delegation tools.
        Assert.All(chatClient.OfferedTools, names => Assert.DoesNotContain(AgentAsToolProvider.StartToolName, names));

        chatClient.Release("task 1", "first done");
        var waited = await work.WaitAsync(parent, parentSession, [handles[0]], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.StartsWith($"Subagent '{handles[0]}' completed.", waited, StringComparison.Ordinal);
        Assert.Contains("first done", waited, StringComparison.Ordinal);

        await StartAsync(tools, "task 5");
        await chatClient.EnteredAsync("task 5");
        Assert.Equal(DelegatedWorkState.MaxActiveWorkers, work.ActiveCount);
    }

    [Fact]
    public async Task InterruptAndContinue_KeepTheWorkerHistoryAndReportActualState()
    {
        var chatClient = new HeldChatClient();
        await using var work = new DelegatedWorkState(CancellationToken.None);
        var (parent, parentSession, provider) = await CreateSelfDelegationAsync(chatClient, work);
        var tools = await GetToolsAsync(provider, parent, parentSession);

        var handle = await StartAsync(tools, "collect sources");
        await chatClient.EnteredAsync("collect sources");
        var interrupted = await InvokeToolAsync(tools, AgentAsToolProvider.InterruptToolName, new() { ["handle"] = handle });
        Assert.StartsWith($"Subagent '{handle}' interrupted.", interrupted, StringComparison.Ordinal);
        Assert.Equal(0, work.ActiveCount);

        var status = await InvokeToolAsync(tools, AgentAsToolProvider.StatusToolName, new());
        Assert.Contains($"{handle} (sources): interrupted", status, StringComparison.Ordinal);

        await InvokeToolAsync(tools, AgentAsToolProvider.ContinueToolName,
            new() { ["handle"] = handle, ["message"] = "finish the list" });
        await chatClient.EnteredAsync("finish the list");
        chatClient.Release("finish the list", "list ready");
        var waited = await work.WaitAsync(parent, parentSession, [handle], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.StartsWith($"Subagent '{handle}' completed.", waited, StringComparison.Ordinal);

        Assert.Contains(chatClient.Calls[^1], message => message.Text == "collect sources");
        var conversation = Assert.Single(await provider.GetConversationsAsync(parent, parentSession, work));
        Assert.Equal(DelegatedWorkerStatus.Completed, conversation.Status);
        Assert.Equal(
            ["collect sources", "finish the list", "list ready"],
            conversation.Messages.Where(message => message.Role != ChatRole.System).Select(message => message.Text));
    }

    [Fact]
    public async Task Status_ForUnnamedCustomAgentWork_ShowsOnlyTheHandle()
    {
        await using var work = new DelegatedWorkState(CancellationToken.None);
        var child = CreateAgent("child", "Researcher", new RecordingChatClient());
        var provider = new AgentAsToolProvider([child.Id], new AIAgentCollection { child }, () => work);
        var parentSession = await child.CreateSessionAsync();
        var tools = await GetToolsAsync(provider, child, parentSession);

        // Custom agent functions come first, before the lifecycle tools.
        var started = await InvokeToolAsync(tools, tools[0].Name, new() { ["query"] = "check facts" });
        var handle = started.Split('\'')[1];
        await work.WaitAsync(child, parentSession, [handle], TimeSpan.FromSeconds(30), CancellationToken.None);

        var status = await InvokeToolAsync(tools, AgentAsToolProvider.StatusToolName, new());
        Assert.StartsWith($"{handle}: completed", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continue_AfterInterruptDuringApprovedTool_DoesNotRejectTheDroppedCall()
    {
        var toolEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowTool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            toolEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, "slow_tool"));
        var chatClient = new SlowToolChatClient();
        await using var work = new DelegatedWorkState(CancellationToken.None);
        ChatClientAgent? worker = null;
        var provider = new AutoApprovingProvider(() => work, _ => Task.FromResult(worker!));
        worker = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Id = "main",
            Name = "Main",
            AIContextProviders = [provider],
            ChatOptions = new ChatOptions { Tools = [slowTool] }
        });
        var parent = CreateAgent("main", "Main", new RecordingChatClient());
        var parentSession = await parent.CreateSessionAsync();
        var tools = await GetToolsAsync(provider, parent, parentSession);

        var handle = await StartAsync(tools, "collect sources");
        await toolEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await InvokeToolAsync(tools, AgentAsToolProvider.InterruptToolName, new() { ["handle"] = handle });
        await InvokeToolAsync(tools, AgentAsToolProvider.ContinueToolName,
            new() { ["handle"] = handle, ["message"] = "finish the list" });
        var waited = await work.WaitAsync(parent, parentSession, [handle], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.StartsWith($"Subagent '{handle}' completed.", waited, StringComparison.Ordinal);
        Assert.DoesNotContain(chatClient.Calls[^1], message => message.Contents.OfType<FunctionResultContent>().Any());
    }

    [Fact]
    public async Task Continue_AfterInterrupt_KeepsAMessageTheWorkerAlreadyReadExactlyOnce()
    {
        var noteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var note = AIFunctionFactory.Create(async () =>
        {
            noteEntered.TrySetResult();
            await releaseNote.Task;
            return "noted";
        }, "note_tool");
        var chatClient = new NoteThenHoldChatClient();
        await using var work = new DelegatedWorkState(CancellationToken.None);
        ChatClientAgent? worker = null;
        var provider = new AgentAsToolProvider([], new AIAgentCollection(), () => work, "main", _ => Task.FromResult(worker!));
        worker = new ChatClientAgent(chatClient.AsBuilder().UseMessageInjection().Build(), new ChatClientAgentOptions
        {
            Id = "main",
            Name = "Main",
            AIContextProviders = [provider],
            ChatOptions = new ChatOptions { Tools = [note] }
        });
        var parent = CreateAgent("main", "Main", new RecordingChatClient());
        var parentSession = await parent.CreateSessionAsync();
        var tools = await GetToolsAsync(provider, parent, parentSession);

        var handle = await StartAsync(tools, "collect sources");
        await noteEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await InvokeToolAsync(tools, AgentAsToolProvider.MessageToolName,
            new() { ["handle"] = handle, ["message"] = "use the second source" });
        releaseNote.SetResult();
        await chatClient.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await InvokeToolAsync(tools, AgentAsToolProvider.InterruptToolName, new() { ["handle"] = handle });
        await InvokeToolAsync(tools, AgentAsToolProvider.ContinueToolName,
            new() { ["handle"] = handle, ["message"] = "finish the list" });
        await work.WaitAsync(parent, parentSession, [handle], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Contains(chatClient.Calls[1], message => message.Text == "use the second source");
        Assert.Single(chatClient.Calls[^1], message => message.Text == "use the second source");
    }

    [Fact]
    public async Task CustomAgent_WithSameConversationNameWhileRunning_KeepsOneWorker()
    {
        var chatClient = new HeldChatClient();
        await using var work = new DelegatedWorkState(CancellationToken.None);
        var child = CreateAgent("child", "Researcher", chatClient);
        var provider = new AgentAsToolProvider([child.Id], new AIAgentCollection { child }, () => work);
        var parentSession = await child.CreateSessionAsync();
        var tools = await GetToolsAsync(provider, child, parentSession);

        await InvokeToolAsync(tools, tools[0].Name, new() { ["query"] = "first", ["conversation"] = "alpha" });
        var repeated = await Assert.ThrowsAsync<ToolFailureException>(() =>
            InvokeToolAsync(tools, tools[0].Name, new() { ["query"] = "second", ["conversation"] = "alpha" }));

        Assert.Contains("is still running", repeated.Message, StringComparison.Ordinal);
        Assert.Single(work.GetWorkers(parentSession));
        chatClient.Release("first", "done");
    }

    [Fact]
    public async Task Continue_BeforeSettlement_ReturnsTheUndeliveredResultOnce()
    {
        var chatClient = new HeldChatClient();
        await using var work = new DelegatedWorkState(CancellationToken.None);
        var (parent, parentSession, provider) = await CreateSelfDelegationAsync(chatClient, work);
        var tools = await GetToolsAsync(provider, parent, parentSession);

        var handle = await StartAsync(tools, "collect sources");
        chatClient.Release("collect sources", "sources ready");
        var worker = work.Find(parentSession, handle)!;
        Assert.True(SpinWait.SpinUntil(() => !worker.IsActive, TimeSpan.FromSeconds(30)));

        var continued = await InvokeToolAsync(tools, AgentAsToolProvider.ContinueToolName,
            new() { ["handle"] = handle, ["message"] = "finish the list" });
        chatClient.Release("finish the list", "list ready");
        var settled = await work.SettleAsync(parent, parentSession, null, CancellationToken.None);

        Assert.Contains("Previous result, completed:\nsources ready", continued, StringComparison.Ordinal);
        var result = Assert.Single(settled!);
        Assert.Contains("list ready", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sources ready", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_AfterTheFinalModelCall_IsRefusedDuringCleanup()
    {
        var chatClient = new HeldChatClient();
        await using var work = new DelegatedWorkState(CancellationToken.None);
        var (parent, parentSession, provider) = await CreateSelfDelegationAsync(chatClient.AsBuilder().UseMessageInjection().Build(), work);
        var tools = await GetToolsAsync(provider, parent, parentSession);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handle = await StartAsync(tools, "collect sources");
        work.Find(parentSession, handle)!.OnFinished(async () =>
        {
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task;
        });
        chatClient.Release("collect sources", "sources ready");
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var refused = await Assert.ThrowsAsync<ToolFailureException>(() => InvokeToolAsync(tools, AgentAsToolProvider.MessageToolName,
            new() { ["handle"] = handle, ["message"] = "use the second source" }));
        releaseCleanup.SetResult();

        Assert.Contains("has finished", refused.Message, StringComparison.Ordinal);
    }

    private static async Task<(ChatClientAgent Parent, AgentSession Session, AgentAsToolProvider Provider)> CreateSelfDelegationAsync(
        IChatClient workerClient, DelegatedWorkState work)
    {
        ChatClientAgent? worker = null;
        var provider = new AgentAsToolProvider([], new AIAgentCollection(), () => work, "main",
            _ => Task.FromResult(worker!));
        worker = new ChatClientAgent(workerClient,
            new ChatClientAgentOptions { Id = "main", Name = "Main", AIContextProviders = [provider] });
        var parent = CreateAgent("main", "Main", new RecordingChatClient());
        return (parent, await parent.CreateSessionAsync(), provider);
    }

    // Names the worker after the last word of its task and returns the handle quoted in the start result.
    private static async Task<string> StartAsync(IReadOnlyList<AIFunction> tools, string task)
    {
        var started = await InvokeToolAsync(tools, AgentAsToolProvider.StartToolName,
            new() { ["task"] = task, ["name"] = task.Split(' ')[^1] });
        return started.Split('\'')[1];
    }

    private static async Task<IReadOnlyList<AIFunction>> GetToolsAsync(
        AgentAsToolProvider provider, AIAgent invokingAgent, AgentSession? parentSession)
    {
        var method = typeof(AgentAsToolProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var invokingContext = new AIContextProvider.InvokingContext(invokingAgent, parentSession, new AIContext());
        var result = method.Invoke(provider, [invokingContext, CancellationToken.None]);
        var context = await Assert.IsType<ValueTask<AIContext>>(result).ConfigureAwait(false);
        return [.. context.Tools!.Cast<AIFunction>()];
    }

    private static async Task<string> InvokeToolAsync(IReadOnlyList<AIFunction> tools, string name, AIFunctionArguments arguments)
    {
        var tool = Assert.Single(tools, tool => tool.Name == name);
        var result = Assert.IsType<JsonElement>(await tool.InvokeAsync(arguments, CancellationToken.None));
        return result.GetString()!;
    }

    private sealed class RecordingProvider(string childId, AIAgentCollection agents) : AgentAsToolProvider([childId], agents)
    {
        public List<AgentSession> Sessions { get; } = [];

        protected override Task<AgentResponse> RunAgentAsync(ChatClientAgent agent, IEnumerable<ChatMessage> messages,
            AgentSession session, AgentRunOptions? options, Action<AgentResponseUpdate>? onUpdate, CancellationToken cancellationToken)
        {
            Assert.NotNull(session);
            Sessions.Add(session);
            return base.RunAgentAsync(agent, messages, session, options, onUpdate, cancellationToken);
        }
    }

    // Approves every request and runs again, as the host's approval loop does under auto-approve.
    private sealed class AutoApprovingProvider(Func<DelegatedWorkState?> work, Func<CancellationToken, Task<ChatClientAgent>> createWorker)
        : AgentAsToolProvider([], new AIAgentCollection(), work, "main", createWorker)
    {
        protected override async Task<AgentResponse> RunAgentAsync(ChatClientAgent agent, IEnumerable<ChatMessage> messages,
            AgentSession session, AgentRunOptions? options, Action<AgentResponseUpdate>? onUpdate, CancellationToken cancellationToken)
        {
            var response = await base.RunAgentAsync(agent, messages, session, options, onUpdate, cancellationToken);
            List<AIContent> approvals = [.. response.Messages.SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>().Select(request => request.CreateResponse(approved: true))];
            return approvals.Count == 0
                ? response
                : await base.RunAgentAsync(agent, [new ChatMessage(ChatRole.User, approvals)], session, options, onUpdate, cancellationToken);
        }
    }

    /// <summary>Calls slow_tool for the first task and answers any later request with text.</summary>
    private sealed class SlowToolChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

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
            lock (Calls)
            {
                Calls.Add(call);
            }

            var latest = call.Last(message => message.Role == ChatRole.User && !string.IsNullOrEmpty(message.Text)).Text;
            AIContent reply = latest == "collect sources"
                ? new FunctionCallContent("call_slow", "slow_tool")
                : new TextContent("list ready");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [reply])));
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

    /// <summary>Calls note_tool for the task, then holds the next call until it is cancelled.</summary>
    private sealed class NoteThenHoldChatClient : IChatClient
    {
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            int count;
            lock (Calls)
            {
                Calls.Add([.. messages]);
                count = Calls.Count;
            }

            if (count == 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_note", "note_tool")]));
            }

            if (count == 2)
            {
                Held.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "list ready"));
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

    private static ChatClientAgent CreateAgent(string id, string name, IChatClient chatClient) =>
        new(chatClient, new ChatClientAgentOptions { Id = id, Name = name });

    private static async Task<object?> InvokeAsync(
        AgentAsToolProvider provider,
        AIAgent invokingAgent,
        AgentSession? parentSession,
        string query,
        string? conversation = null)
    {
        var function = Assert.Single(await GetToolsAsync(provider, invokingAgent, parentSession).ConfigureAwait(false));
        var arguments = new AIFunctionArguments { ["query"] = query };
        if (conversation is not null)
        {
            arguments["conversation"] = conversation;
        }

        return await function.InvokeAsync(arguments, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Holds each call at a barrier keyed by its latest user message until the test releases it.</summary>
    private sealed class HeldChatClient : IChatClient
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _replies = new();

        public ConcurrentQueue<IReadOnlyList<string>> OfferedTools { get; } = new();
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public Task EnteredAsync(string text) => Gate(_entered, text).Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Release(string text, string reply) => Gate(_replies, text).SetResult(reply);

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = messages.ToList();
            var key = call.Last(message => message.Role == ChatRole.User).Text;
            lock (Calls)
            {
                Calls.Add(call);
            }

            OfferedTools.Enqueue([.. options?.Tools?.Select(tool => tool.Name) ?? []]);
            Gate(_entered, key).TrySetResult();
            var reply = await Gate(_replies, key).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply));
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

        private static TaskCompletionSource<T> Gate<T>(ConcurrentDictionary<string, TaskCompletionSource<T>> gates, string key) =>
            gates.GetOrAdd(key, static _ => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously));

        private static TaskCompletionSource Gate(ConcurrentDictionary<string, TaskCompletionSource> gates, string key) =>
            gates.GetOrAdd(key, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
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
