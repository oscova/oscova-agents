#if DEBUG
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Llm;
using Oscova.Agents.Providers;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Host.Server.Security;

/// <summary>Answers chat from canned replies so test hosts get deterministic agent replies without a real model.</summary>
internal sealed class DebugTestModelProvider : IModelProvider
{
    public const string ProviderType = "test";
    public const string ModelId = "test-1";

    /// <summary>Registers the provider only when <c>DebugAutoLogin:Seed:Provider</c> selects it.</summary>
    public static void AddWhenSeeded(IServiceCollection services, IConfiguration configuration)
    {
        if (string.Equals(configuration["DebugAutoLogin:Seed:Provider"]?.Trim(), ProviderType, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IModelProvider, DebugTestModelProvider>();
        }
    }

    public string Type => ProviderType;
    public string? DefaultEndpoint => null;
    public string? DefaultModel => ModelId;

    // Replies are canned in process, so nothing leaves the machine.
    public bool? DefaultIsLocalEndpoint => true;
    public bool SupportsEmbedding => true;

    public ValueTask<IChatClient> CreateClientAsync(ModelProviderConfig config, string? apiKey = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IChatClient>(new CannedReplyChatClient());

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(ModelProviderConfig config, string? apiKey = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IEmbeddingGenerator<string, Embedding<float>>>(new DeterministicEmbeddingGenerator());

    public Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(ModelProviderConfig config, string? apiKey = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ModelDescriptor>>([new ModelDescriptor { Id = ModelId }]);

    public Task<(bool Success, string Message)> TestAsync(ModelProviderConfig config, string? apiKey = null, CancellationToken cancellationToken = default)
        => Task.FromResult((true, "Test model provider is available."));

    // Exercise real indexing and storage without contacting an embedding service.
    private sealed class DeterministicEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                embeddings.Add(new Embedding<float>(new float[] { 1, value.Length % 7 + 1, value.Length % 11 + 1 }));
            }

            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class CannedReplyChatClient : IChatClient
    {
        private static readonly Dictionary<string, string> Replies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ping"] = "pong",
            ["How are you?"] = "I am fine",
            ["test 1"] = "test passed"
        };

        // Unmatched messages, including title prompts, echo back so tests can still assert on them.
        private static string Reply(IEnumerable<ChatMessage> messages)
        {
            var text = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text.Trim() ?? string.Empty;
            return Replies.TryGetValue(text, out var reply) ? reply : "echo: " + text;
        }

        private const string WorkerQuestionPrompt = "test worker question";
        private const string WorkerTask = "Check the worker question diagnostic.";
        private const string DiscoveryResumePrompt = "test discovery resume";

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            var last = snapshot.LastOrDefault();
            ChatMessage response;
            var isParent = options?.Tools?.Any(tool => tool.Name == AgentAsToolProvider.StartToolName) == true;
            var prompt = Array.FindLastIndex(snapshot, message => message.Role == ChatRole.User && message.Text.Trim() == WorkerQuestionPrompt);
            var userText = Array.FindLastIndex(snapshot, message => message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text));
            if (userText >= 0 && snapshot[userText].Text.Trim() == DiscoveryResumePrompt)
            {
                response = DiscoveryResumeStep(snapshot[userText..]);
            }
            else if (options?.AdditionalProperties?.TryGetValue("think", out var thinking) == true && thinking is false &&
                last?.Text.Contains("test compact seed cedar", StringComparison.Ordinal) == true)
            {
                response = new(ChatRole.Assistant, "The retained code word is cedar.");
            }
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test compact recall")
            {
                var summary = snapshot.Any(message => message.Text.StartsWith("[Conversation Summary]", StringComparison.Ordinal) &&
                    message.Text.Contains("cedar", StringComparison.Ordinal));
                response = new(ChatRole.Assistant, summary ? "summary retained cedar" : "summary missing");
            }
            else if (last?.Role == ChatRole.User && last.Text.StartsWith("test compact batch", StringComparison.Ordinal))
            {
                response = new(ChatRole.Assistant, [new TextReasoningContent("Checking the intake batch."), new TextContent("batch noted")]);
            }
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test memory save")
            {
                response = Call(ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.SaveMemoryAsync)), new()
                {
                    ["conceptId"] = "harness-preference",
                    ["frontmatter"] = new Dictionary<string, object?> { ["type"] = "preference", ["title"] = "Harness preference", ["description"] = "Appointment preference" },
                    ["content"] = "The user prefers morning appointments."
                });
            }
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test memory read")
            {
                response = Call(ToolNaming.BuildToolsetQualifiedName<MemoryToolset>(nameof(MemoryToolset.ReadMemoryAsync)), new() { ["document"] = "harness-preference" });
            }
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test approval")
            {
                response = Call(ToolNaming.BuildToolsetQualifiedName<DirectoryToolset>(nameof(DirectoryToolset.ListDirectory)), new() { ["path"] = PathHelper.SelfAgentPath });
            }
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test guide load")
            {
                response = Call(AgentSkillsProvider.LoadSkillToolName, new() { ["skillName"] = ProductGuideSkillSeed.SkillName });
            }
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test registry install")
            {
                // Fresh main agents discover the installer before invoking it.
                response = Call(ToolSearchProvider.SearchToolName,
                    new() { ["query"] = ToolNaming.BuildToolsetQualifiedName<SkillRegistryToolset>(nameof(SkillRegistryToolset.InstallSkillAsync)) });
            }
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test registry load")
            {
                response = Call(AgentSkillsProvider.LoadSkillToolName, new() { ["skillName"] = "pdf-official" });
            }
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test node write")
            {
                response = Call(ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.WriteFile)),
                    new() { ["file_path"] = "harness-check.txt", ["content"] = "node-write-ok" });
            }
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test node check")
            {
                var command = OperatingSystem.IsWindows()
                    ? "if ((Get-Content -Raw harness-check.txt) -ne 'node-write-ok') { throw 'Wrong file content' }; 'node-check-passed'"
                    : "test \"$(cat harness-check.txt)\" = node-write-ok && printf node-check-passed";
                response = Call(ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.ExecCommandAsync)), new() { ["command"] = command });
            }
            else if (isParent && prompt >= 0)
            {
                var calls = snapshot.Skip(prompt).SelectMany(message => message.Contents).OfType<FunctionCallContent>().Count();
                response = WorkerQuestionStep(calls, snapshot);
            }
            else if (snapshot.Any(message => message.Role == ChatRole.User && message.Text.Contains(WorkerTask, StringComparison.Ordinal)))
            {
                // The worker finishes after the parent posts its question, so its result wakes the parent.
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                response = new(ChatRole.Assistant, "worker done");
            }
            // This explicit diagnostic calls the test MCP server's session-state tool through the ordinary runtime.
            else if (last?.Role == ChatRole.User && last.Text.Trim() == "test mcp")
            {
                var tool = options?.Tools?.OfType<AIFunction>().SingleOrDefault(function =>
                    function.Name.EndsWith(ToolNaming.WireSeparator + "test_session_state", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("The MCP diagnostic requires the test MCP server's session-state tool.");
                response = Call(tool.Name, []);
            }
            // This explicit diagnostic invokes an available delegation tool through the ordinary runtime.
            else if (last?.Role == ChatRole.User && last.Text.Trim() is "test delegation" or "test delegation approval")
            {
                var tool = options?.Tools?.OfType<AIFunction>().Single(function =>
                    function.JsonSchema.GetProperty("properties").TryGetProperty("destination", out _) &&
                    function.JsonSchema.GetProperty("properties").TryGetProperty("includeChatHistory", out _))
                    ?? throw new InvalidOperationException("The delegation diagnostic requires one assigned agent tool.");
                response = new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool.Name,
                    new Dictionary<string, object?> { ["query"] = last.Text.Trim() == "test delegation approval" ? "test approval" : "ping", ["destination"] = "delegated" })]);
            }
            else if (last?.Contents.OfType<FunctionResultContent>().SingleOrDefault() is { } result)
            {
                if (snapshot.SelectMany(message => message.Contents).OfType<FunctionCallContent>().LastOrDefault()?.Name == ToolSearchProvider.SearchToolName)
                {
                    response = Call(ToolNaming.BuildToolsetQualifiedName<SkillRegistryToolset>(nameof(SkillRegistryToolset.InstallSkillAsync)),
                        new() { ["owner"] = "sickn33", ["repo"] = "agentic-awesome-skills", ["branch"] = "main", ["skillPath"] = "skills/pdf-official" });
                }
                else
                {
                    response = new(ChatRole.Assistant, result.Exception?.Message ?? result.Result?.ToString() ?? string.Empty);
                }
            }
            else
            {
                response = new(ChatRole.Assistant, Reply(snapshot));
            }

            response.MessageId = Guid.NewGuid().ToString("N");
            return new ChatResponse(response) { ModelId = ModelId };
        }

        // The parent starts a worker, posts a question, sleeps when the worker result wakes it, then echoes the answer.
        private static ChatMessage WorkerQuestionStep(int calls, ChatMessage[] snapshot) => calls switch
        {
            0 => Call(AgentAsToolProvider.StartToolName, new() { ["task"] = WorkerTask, ["name"] = "checker" }),
            1 => Call(UserQuestions.QualifiedToolName, new()
            {
                ["questions"] = new UserQuestions.Question[]
                    { new("direction", "Direction", "Which direction?", [new("left", "Left"), new("right", "Right")]) }
            }),
            2 => Call(WaitToolset.QualifiedToolName, new() { ["durationSeconds"] = 60, ["reason"] = "Waiting for the direction answer." }),
            _ => new(ChatRole.Assistant, "answered " + snapshot.Last(message => message.Role == ChatRole.User).Text.Split('\n')[^1])
        };

        // Discovery loads the family, creation pauses for approval, and the resumed run calls another loaded tool.
        private static ChatMessage DiscoveryResumeStep(ChatMessage[] turn)
        {
            var results = turn.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            return results.Length switch
            {
                0 => Call(ToolSearchProvider.SearchToolName, new() { ["query"] = "visualization" }),
                1 => Call(ToolNaming.BuildToolsetQualifiedName<VisualizationToolset>(nameof(VisualizationToolset.CreateAsync)),
                    new() { ["html"] = "<p>discovery resume</p>" }),
                2 => Call(VisualizationToolset.QualifiedToolName,
                    new() { ["id"] = JsonDocument.Parse(results[1].Result!.ToString()!).RootElement.GetProperty("id").GetString() }),
                _ => new(ChatRole.Assistant, results[^1].Exception?.Message ?? results[^1].Result?.ToString() ?? string.Empty)
            };
        }

        private static ChatMessage Call(string name, Dictionary<string, object?> arguments)
            => new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments)]);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates()) { yield return update; }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
#endif
