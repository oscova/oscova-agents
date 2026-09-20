using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Persistence;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Tests.Framework.Conversations;

public sealed class ChatThreadRepositoryTests : IDisposable
{
    private readonly string _usersRoot =
        Path.Combine(Path.GetTempPath(), $"oscova-chat-repository-{Guid.NewGuid():N}");
    private readonly ChatThreadRepository _repository;

    public ChatThreadRepositoryTests()
    {
        _repository = new ChatThreadRepository(
            () => _usersRoot,
            new FileSessionIndexCoordinator(),
            new SecretRedactionService(),
            new PassThroughContentProtector(),
            new InMemoryAgentDefinitionRepository(),
            NullLogger<ChatThreadRepository>.Instance);
    }

    /// <summary>Display replacement preserves the session and other messages and never restores a deleted thread.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserQuestion_DisplayReplacement_PreservesConversation(bool incognito)
    {
        var thread = new ChatThread
        {
            Id = "question-replacement", IsIncognito = incognito, Title = "Questions",
            SessionElement = JsonSerializer.SerializeToElement(new { receipt = "original" }),
            DisplayMessages = [new() { MessageId = "first", Text = "Original" }, new() { MessageId = "second", Text = "Unchanged" }]
        };
        await _repository.SaveThreadAsync("user-1", thread);
        var replacement = new ChatDisplayMessage { MessageId = "first", Text = "Replaced" };
        Assert.False(await _repository.TryReplaceDisplayMessageAsync("other-user", thread.Id, replacement));
        Assert.True(await _repository.TryReplaceDisplayMessageAsync("user-1", thread.Id, replacement));
        var stored = (await _repository.GetThreadAsync("user-1", thread.Id))!;
        Assert.Equal(["Replaced", "Unchanged"], stored.DisplayMessages.Select(message => message.Text));
        Assert.Equal("original", stored.SessionElement!.Value.GetProperty("receipt").GetString());
        Assert.Equal("Questions", stored.Title);
        await _repository.DeleteThreadAsync("user-1", thread.Id);
        Assert.False(await _repository.TryReplaceDisplayMessageAsync("user-1", thread.Id, replacement));
        Assert.Null(await _repository.GetThreadAsync("user-1", thread.Id));
    }

    /// <summary>Encrypted display updates preserve protected storage and refuse a locked vault.</summary>
    [Fact]
    public async Task UserQuestion_DisplayReplacement_PreservesEncryptedStorage()
    {
        var protector = new PassThroughContentProtector();
        var agents = new InMemoryAgentDefinitionRepository();
        await agents.SaveAgentAsync("user-1", new AgentDefinition { Id = "private", IsConfidential = true });
        var repository = CreateRepository(protector, agents);
        var thread = new ChatThread
        {
            AgentId = "private", DisplayMessages = [new() { MessageId = "card", Text = "Question" }],
            SessionElement = JsonSerializer.SerializeToElement(new { receipt = "original" })
        };
        await repository.SaveThreadAsync("user-1", thread);
        var replacement = new ChatDisplayMessage { MessageId = "card", Text = "Closed question" };
        protector.Available = false;
        Assert.False(await repository.TryReplaceDisplayMessageAsync("user-1", thread.Id, replacement));
        protector.Available = true;
        Assert.True(await repository.TryReplaceDisplayMessageAsync("user-1", thread.Id, replacement));
        var saved = (await repository.GetThreadAsync("user-1", thread.Id))!;
        Assert.True(saved.IsEncrypted);
        Assert.Equal("Closed question", Assert.Single(saved.DisplayMessages).Text);
        Assert.Equal("original", saved.SessionElement!.Value.GetProperty("receipt").GetString());
        var lines = await File.ReadAllLinesAsync(Path.Combine(_usersRoot, "user-1", "chat", thread.Id + ".jsonl"));
        Assert.All(lines, line => Assert.Contains("\"IsEncrypted\":true", line, StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_WhenUsersRootResolverIsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ChatThreadRepository(
                null!,
                new FileSessionIndexCoordinator(),
                new SecretRedactionService(),
                new PassThroughContentProtector(),
                new InMemoryAgentDefinitionRepository(),
                NullLogger<ChatThreadRepository>.Instance));
    }

    [Fact]
    public async Task SaveThreadAsync_WhenUsersRootChanges_UsesCurrentRoot()
    {
        var firstRoot = Path.Combine(_usersRoot, "first");
        var secondRoot = Path.Combine(_usersRoot, "second");
        var currentRoot = firstRoot;
        var repository = new ChatThreadRepository(
            () => currentRoot,
            new FileSessionIndexCoordinator(),
            new SecretRedactionService(),
            new PassThroughContentProtector(),
            new InMemoryAgentDefinitionRepository(),
            NullLogger<ChatThreadRepository>.Instance);
        var firstThread = new ChatThread { Id = "first-thread" };
        var secondThread = new ChatThread { Id = "second-thread" };

        await repository.SaveThreadAsync("user-1", firstThread);
        currentRoot = secondRoot;
        await repository.SaveThreadAsync("user-1", secondThread);

        Assert.True(File.Exists(Path.Combine(firstRoot, "user-1", "chat", "first-thread.jsonl")));
        Assert.False(File.Exists(Path.Combine(firstRoot, "user-1", "chat", "second-thread.jsonl")));
        Assert.True(File.Exists(Path.Combine(secondRoot, "user-1", "chat", "second-thread.jsonl")));
        Assert.False(File.Exists(Path.Combine(secondRoot, "user-1", "chat", "first-thread.jsonl")));
    }

    [Fact]
    public async Task GetThreadsAsync_WithNoThreads_ReturnsEmptyList()
    {
        var result = await _repository.GetThreadsAsync("user-1");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetThreadsAsync_OnlyReturnsCurrentUserThreads()
    {
        await _repository.SaveThreadAsync("user-1", new ChatThread { Title = "Thread A" });
        await _repository.SaveThreadAsync("user-2", new ChatThread { Title = "Thread B" });

        var result = await _repository.GetThreadsAsync("user-1");

        var thread = Assert.Single(result);
        Assert.Equal("Thread A", thread.Title);
    }

    [Fact]
    public async Task GetThreadsAsync_ReturnsThreadsOrderedByUpdatedAtDescending()
    {
        var older = new ChatThread { Title = "Older" };
        await _repository.SaveThreadAsync("user-1", older);
        await Task.Delay(10);
        var newer = new ChatThread { Title = "Newer" };
        await _repository.SaveThreadAsync("user-1", newer);

        var result = await _repository.GetThreadsAsync("user-1");

        Assert.Equal(["Newer", "Older"], result.Select(thread => thread.Title));
    }

    [Fact]
    public async Task GetThreadAsync_RoundTripsDisplayMessagesSessionAndSystemInstruction()
    {
        var session = JsonDocument.Parse("{\"key\":\"value\",\"count\":42}").RootElement.Clone();
        var thread = new ChatThread
        {
            Title = "Chat",
            AgentId = "agent-1",
            AgentName = "Assistant",
            SystemInstruction = "Use stored-secret-value carefully.",
            SessionElement = session,
            DisplayMessages =
            [
                new ChatDisplayMessage
                {
                    Channel = ChatChannels.Discord,
                    From = MessageOwner.User,
                    Text = "Hello stored-secret-value.",
                    IsInteractionResponse = true
                },
                new ChatDisplayMessage
                {
                    From = MessageOwner.Bot,
                    Text = "Hi there",
                    Segments =
                    [
                        new ChatSegment(ChatSegmentType.Text, ChatSegmentState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Segment stored-secret-value.", null)
                    ]
                }
            ]
        };

        await _repository.SaveThreadAsync("user-1", thread);

        var result = await _repository.GetThreadAsync("user-1", thread.Id);

        Assert.NotNull(result);
        Assert.Equal("Chat", result!.Title);
        Assert.Equal("Assistant", result.AgentName);
        Assert.True(result.SessionElement.HasValue);
        Assert.Equal("value", result.SessionElement.Value.GetProperty("key").GetString());
        Assert.Equal(42, result.SessionElement.Value.GetProperty("count").GetInt32());
        Assert.Equal("Use [REDACTED] carefully.", result.SystemInstruction);
        Assert.Equal(2, result.DisplayMessages.Count);
        Assert.Equal(ChatChannels.Discord, result.DisplayMessages[0].Channel);
        Assert.Equal("Hello [REDACTED].", result.DisplayMessages[0].Text);
        Assert.True(result.DisplayMessages[0].IsInteractionResponse);
        Assert.Equal("Segment [REDACTED].", result.DisplayMessages[1].Segments.Single().Text);
    }

    [Fact]
    public async Task SaveThreadAsync_ForConfidentialAgent_ProtectsEveryArtifactAndRoundTrips()
    {
        var protector = new PassThroughContentProtector();
        var agents = new InMemoryAgentDefinitionRepository();
        await agents.SaveAgentAsync("user-1", new AgentDefinition
        {
            Id = "private-agent",
            IsConfidential = true
        });
        var repository = CreateRepository(protector, agents);
        var thread = new ChatThread
        {
            Title = "confidential-title",
            AgentId = "private-agent",
            SystemInstruction = "confidential-instruction",
            SessionElement = JsonSerializer.SerializeToElement(new { value = "confidential-session" }),
            DisplayMessages =
            [
                new ChatDisplayMessage { From = MessageOwner.User, Text = "confidential-message" }
            ]
        };

        await repository.SaveThreadAsync("user-1", thread);

        var raw = string.Join(
            Environment.NewLine,
            await File.ReadAllTextAsync(GetIndexPath("user-1")),
            await File.ReadAllTextAsync(GetTranscriptPath("user-1", thread.Id)),
            await File.ReadAllTextAsync(GetSessionPath("user-1", thread.Id)));
        Assert.DoesNotContain("confidential-title", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential-instruction", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential-message", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential-session", raw, StringComparison.Ordinal);

        var loaded = await repository.GetThreadAsync("user-1", thread.Id);

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsEncrypted);
        Assert.Equal("confidential-title", loaded.Title);
        Assert.Equal("confidential-instruction", loaded.SystemInstruction);
        Assert.Equal("confidential-message", Assert.Single(loaded.DisplayMessages).Text);
        Assert.Equal("confidential-session", loaded.SessionElement?.GetProperty("value").GetString());
    }

    [Fact]
    public async Task SaveThreadAsync_WhenConfidentialStateChanges_RewritesWholeTranscript()
    {
        var protector = new PassThroughContentProtector();
        var agents = new InMemoryAgentDefinitionRepository();
        var agent = new AgentDefinition { Id = "changing-agent" };
        await agents.SaveAgentAsync("user-1", agent);
        var repository = CreateRepository(protector, agents);
        var thread = new ChatThread
        {
            Title = "changing-title",
            AgentId = agent.Id,
            DisplayMessages = [new ChatDisplayMessage { From = MessageOwner.User, Text = "first-message" }]
        };
        await repository.SaveThreadAsync("user-1", thread);
        agent.IsConfidential = true;
        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.Bot, Text = "second-message" });

        await repository.SaveThreadAsync("user-1", thread);

        var encryptedLines = await File.ReadAllLinesAsync(GetTranscriptPath("user-1", thread.Id));
        Assert.Equal(3, encryptedLines.Length);
        Assert.All(encryptedLines, line => Assert.Contains("\"IsEncrypted\":true", line, StringComparison.Ordinal));
        Assert.DoesNotContain("first-message", string.Join(Environment.NewLine, encryptedLines), StringComparison.Ordinal);
        Assert.DoesNotContain("second-message", string.Join(Environment.NewLine, encryptedLines), StringComparison.Ordinal);

        agent.IsConfidential = false;
        await repository.SaveThreadAsync("user-1", thread);
        var plainTranscript = await File.ReadAllTextAsync(GetTranscriptPath("user-1", thread.Id));
        Assert.Contains("first-message", plainTranscript, StringComparison.Ordinal);
        Assert.Contains("second-message", plainTranscript, StringComparison.Ordinal);
        Assert.Equal(2, (await repository.GetThreadAsync("user-1", thread.Id))!.DisplayMessages.Count);
    }

    [Fact]
    public async Task GetThreadsAsync_WhenEncryptedThreadsExcluded_DoesNotOpenProtectedTitles()
    {
        var protector = new PassThroughContentProtector();
        var agents = new InMemoryAgentDefinitionRepository();
        await agents.SaveAgentAsync("user-1", new AgentDefinition
        {
            Id = "private-agent",
            IsConfidential = true
        });
        var repository = CreateRepository(protector, agents);
        await repository.SaveThreadAsync("user-1", new ChatThread
        {
            AgentId = "private-agent",
            Title = "hidden-title"
        });

        var threads = await repository.GetThreadsAsync("user-1", includeEncrypted: false);

        Assert.Empty(threads);
        Assert.Empty(protector.UnprotectCalls);
    }

    [Fact]
    public async Task EncryptedReads_WhenKeyIsLocked_AreUnavailableUntilUnlock()
    {
        var protector = new PassThroughContentProtector();
        var agents = new InMemoryAgentDefinitionRepository();
        await agents.SaveAgentAsync("user-1", new AgentDefinition
        {
            Id = "private-agent",
            IsConfidential = true
        });
        var repository = CreateRepository(protector, agents);
        var thread = new ChatThread
        {
            AgentId = "private-agent",
            Title = "hidden-title",
            DisplayMessages = [new ChatDisplayMessage { From = MessageOwner.User, Text = "hidden-message" }]
        };
        await repository.SaveThreadAsync("user-1", thread);
        protector.Available = false;

        Assert.Empty(await repository.GetThreadsAsync("user-1"));
        Assert.Null(await repository.GetThreadAsync("user-1", thread.Id));
        Assert.Empty(protector.UnprotectCalls);

        protector.Available = true;
        var loaded = await repository.GetThreadAsync("user-1", thread.Id);

        Assert.Equal("hidden-title", loaded?.Title);
        Assert.Equal("hidden-message", Assert.Single(loaded!.DisplayMessages).Text);
    }

    [Theory]
    [InlineData("missing-payload")]
    [InlineData("unopenable-payload")]
    [InlineData("invalid-json")]
    public async Task GetThreadAsync_WhenEncryptedMessageIsCorrupt_Throws(string corruption)
    {
        var protector = new PassThroughContentProtector();
        var agents = new InMemoryAgentDefinitionRepository();
        await agents.SaveAgentAsync("user-1", new AgentDefinition
        {
            Id = "private-agent",
            IsConfidential = true
        });
        var repository = CreateRepository(protector, agents);
        var thread = new ChatThread
        {
            AgentId = "private-agent",
            DisplayMessages = [new ChatDisplayMessage { From = MessageOwner.User, Text = "secret" }]
        };
        await repository.SaveThreadAsync("user-1", thread);
        var path = GetTranscriptPath("user-1", thread.Id);
        var lines = await File.ReadAllLinesAsync(path);
        var message = JsonNode.Parse(lines[1])!.AsObject();
        message.Remove("Message");
        message["IsEncrypted"] = true;
        message["ProtectedPayload"] = corruption switch
        {
            "missing-payload" => null,
            "unopenable-payload" => "invalid",
            _ => protector.Protect("user-1", $"chat\0{thread.Id}\0transcript", "not-json")
        };
        lines[1] = message.ToJsonString();
        await File.WriteAllLinesAsync(path, lines);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetThreadAsync("user-1", thread.Id));
    }

    [SkippableFact]
    public async Task CaseVariant_UsesCanonicalTranscriptAndSessionFiles()
    {
        Directory.CreateDirectory(_usersRoot);
        var caseProbePath = Path.Combine(_usersRoot, "case-probe");
        await File.WriteAllTextAsync(caseProbePath, "probe");
        var isCaseSensitive = !File.Exists(Path.Combine(_usersRoot, "CASE-PROBE"));
        File.Delete(caseProbePath);
        Skip.IfNot(isCaseSensitive, "Requires a case-sensitive filesystem.");

        var session = JsonDocument.Parse("{\"version\":1}").RootElement.Clone();
        var thread = new ChatThread
        {
            Id = "thread-ab12",
            AgentId = "agent-1",
            SessionElement = session,
            DisplayMessages =
            [
                new ChatDisplayMessage { From = MessageOwner.User, Text = "one" },
                new ChatDisplayMessage { From = MessageOwner.Bot, Text = "two" }
            ]
        };
        await _repository.SaveThreadAsync("user-1", thread);
        var canonicalTranscriptPath = GetTranscriptPath("user-1", thread.Id);
        var canonicalSessionPath = GetSessionPath("user-1", thread.Id);

        var loaded = await _repository.GetThreadAsync("user-1", "THREAD-AB12");
        Assert.NotNull(loaded);
        var loadedMessageCount = loaded!.DisplayMessages.Count;
        var loadedSessionVersion = loaded.SessionElement?.GetProperty("version").GetInt32();
        loaded.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "three" });
        await _repository.SaveThreadAsync("user-1", loaded);
        var updatedSession = JsonDocument.Parse("{\"version\":2}").RootElement.Clone();
        await _repository.SaveSessionElementAsync("user-1", "THREAD-AB12", updatedSession, thread.AgentId);

        var reloaded = await _repository.GetThreadAsync("user-1", thread.Id);
        Assert.NotNull(reloaded);
        var reloadedMessages = reloaded!.DisplayMessages.Select(message => message.Text).ToArray();
        var reloadedSessionVersion = reloaded.SessionElement?.GetProperty("version").GetInt32();
        var sessionDirectory = Path.GetDirectoryName(canonicalSessionPath)!;
        var sessionFileCount = Directory.GetFiles(sessionDirectory, "*.session.json").Length;

        await _repository.DeleteThreadAsync("user-1", "THREAD-AB12");

        Assert.Equal(2, loadedMessageCount);
        Assert.Equal(1, loadedSessionVersion);
        Assert.Equal(["one", "two", "three"], reloadedMessages);
        Assert.Equal(2, reloadedSessionVersion);
        Assert.Equal(1, sessionFileCount);
        Assert.False(File.Exists(canonicalTranscriptPath));
        Assert.False(File.Exists(canonicalSessionPath));
    }

    [Fact]
    public async Task ThreadOperations_WithTraversalId_DoNotEscapeUserChatDirectory()
    {
        const string attackerUserId = "attacker-user";
        const string victimUserId = "victim-user";
        var attackerChatDirectory = Path.GetDirectoryName(GetIndexPath(attackerUserId))!;
        var victimChatDirectory = Path.GetDirectoryName(GetIndexPath(victimUserId))!;
        Directory.CreateDirectory(victimChatDirectory);

        var saveThreadId = Path.GetRelativePath(attackerChatDirectory, Path.Combine(victimChatDirectory, "save-thread"));
        var saveThreadPath = GetTranscriptPath(victimUserId, "save-thread");
        await File.WriteAllTextAsync(saveThreadPath, "victim transcript sentinel");

        var saveSessionId = Path.GetRelativePath(attackerChatDirectory, Path.Combine(victimChatDirectory, "save-session"));
        var saveSessionPath = GetSessionPath(victimUserId, "save-session");
        await File.WriteAllTextAsync(saveSessionPath, "{\"sentinel\":\"session-write\"}");

        var readSessionId = Path.GetRelativePath(attackerChatDirectory, Path.Combine(victimChatDirectory, "read-session"));
        var readSessionPath = GetSessionPath(victimUserId, "read-session");
        await File.WriteAllTextAsync(readSessionPath, "{\"sentinel\":\"session-read\"}");

        var deleteThreadId = Path.GetRelativePath(attackerChatDirectory, Path.Combine(victimChatDirectory, "delete-thread"));
        var deleteTranscriptPath = GetTranscriptPath(victimUserId, "delete-thread");
        var deleteSessionPath = GetSessionPath(victimUserId, "delete-thread");
        await File.WriteAllTextAsync(deleteTranscriptPath, "victim delete transcript sentinel");
        await File.WriteAllTextAsync(deleteSessionPath, "{\"sentinel\":\"delete-session\"}");

        var deleteSessionOnlyId = Path.GetRelativePath(
            attackerChatDirectory,
            Path.Combine(victimChatDirectory, "delete-session-only"));
        var deleteSessionOnlyPath = GetSessionPath(victimUserId, "delete-session-only");
        await File.WriteAllTextAsync(deleteSessionOnlyPath, "{\"sentinel\":\"delete-session-only\"}");

        var saveThreadException = await Record.ExceptionAsync(() => _repository.SaveThreadAsync(
            attackerUserId,
            new ChatThread { Id = saveThreadId }));
        var saveSessionException = await Record.ExceptionAsync(() => _repository.SaveSessionElementAsync(
            attackerUserId,
            saveSessionId,
            JsonDocument.Parse("{\"attacker\":true}").RootElement.Clone(),
            "agent-1"));
        JsonElement? escapedSession = null;
        var readSessionException = await Record.ExceptionAsync(async () =>
            escapedSession = await _repository.GetSessionElementAsync(attackerUserId, readSessionId));
        var deleteThreadException = await Record.ExceptionAsync(() =>
            _repository.DeleteThreadAsync(attackerUserId, deleteThreadId));
        var deleteSessionOnlyException = await Record.ExceptionAsync(() =>
            _repository.DeleteSessionElementAsync(attackerUserId, deleteSessionOnlyId));

        var attackerThreads = await _repository.GetThreadsAsync(attackerUserId);
        var savedTranscript = await File.ReadAllTextAsync(saveThreadPath);
        var savedSession = await File.ReadAllTextAsync(saveSessionPath);
        var deleteTranscriptExists = File.Exists(deleteTranscriptPath);
        var deleteSessionExists = File.Exists(deleteSessionPath);
        var deleteSessionOnlyExists = File.Exists(deleteSessionOnlyPath);
        var traversalIndexRows = attackerThreads.Count(thread => thread.Id.Contains("..", StringComparison.Ordinal));

        Assert.Equal(
            (
                SaveThreadRejected: true,
                SaveSessionRejected: true,
                ReadSessionRejected: true,
                DeleteThreadRejected: true,
                DeleteSessionOnlyRejected: true,
                SavedTranscript: "victim transcript sentinel",
                SavedSession: "{\"sentinel\":\"session-write\"}",
                DeleteTranscriptExists: true,
                DeleteSessionExists: true,
                DeleteSessionOnlyExists: true,
                TraversalIndexRows: 0),
            (
                SaveThreadRejected: saveThreadException is not null,
                SaveSessionRejected: saveSessionException is not null,
                ReadSessionRejected: readSessionException is not null && escapedSession is null,
                DeleteThreadRejected: deleteThreadException is not null,
                DeleteSessionOnlyRejected: deleteSessionOnlyException is not null,
                SavedTranscript: savedTranscript,
                SavedSession: savedSession,
                DeleteTranscriptExists: deleteTranscriptExists,
                DeleteSessionExists: deleteSessionExists,
                DeleteSessionOnlyExists: deleteSessionOnlyExists,
                TraversalIndexRows: traversalIndexRows));
    }

    [Fact]
    public async Task SaveThreadAsync_WhenUserIdEscapesUsersRoot_RejectsWrite()
    {
        var outsideUserDirectory = _usersRoot + "-outside-user";
        var userId = Path.GetRelativePath(_usersRoot, outsideUserDirectory);
        var outsideTranscriptPath = Path.Combine(outsideUserDirectory, "chat", "escaped-thread.jsonl");

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _repository.SaveThreadAsync(
                userId,
                new ChatThread { Id = "escaped-thread" }));

            Assert.False(File.Exists(outsideTranscriptPath));
        }
        finally
        {
            if (Directory.Exists(outsideUserDirectory))
            {
                Directory.Delete(outsideUserDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveThreadAsync_WhenThreadIdIsBlank_RejectsGenericSidecars()
    {
        var thread = new ChatThread
        {
            Id = string.Empty,
            SessionElement = JsonSerializer.SerializeToElement(new { value = "session" })
        };

        var saveException = await Record.ExceptionAsync(() => _repository.SaveThreadAsync("user-1", thread));
        var reloadException = await Record.ExceptionAsync(() => _repository.GetThreadAsync("user-1", string.Empty));

        Assert.IsType<ArgumentException>(saveException);
        Assert.IsType<ArgumentException>(reloadException);
        Assert.False(File.Exists(GetTranscriptPath("user-1", string.Empty)));
        Assert.False(File.Exists(GetSessionPath("user-1", string.Empty)));
        Assert.False(File.Exists(GetIndexPath("user-1")));
    }

    [Fact]
    public async Task SaveThreadAsync_RedactsToolArgumentsWithoutMutatingDisplayMessage()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["apiKey"] = "plain-secret-value",
            ["note"] = "stored-secret-value"
        };
        var segment = new ChatSegment(
            ChatSegmentType.ToolCall,
            ChatSegmentState.Completed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            """{"apiKey":"plain-secret-value","note":"stored-secret-value"}""",
            new FunctionCallContent("call-1", "example_tool", arguments))
        {
            ExecutionState = ToolExecutionState.Completed
        };
        var thread = new ChatThread
        {
            AgentId = "agent-1",
            DisplayMessages =
            [
                new ChatDisplayMessage
                {
                    From = MessageOwner.Bot,
                    Text = "Tool completed.",
                    Segments = [segment]
                }
            ]
        };

        await _repository.SaveThreadAsync("user-1", thread);

        var transcript = await File.ReadAllTextAsync(GetTranscriptPath("user-1", thread.Id));
        var reloaded = await _repository.GetThreadAsync("user-1", thread.Id);
        var persistedSegment = Assert.Single(Assert.Single(reloaded!.DisplayMessages).Segments);

        Assert.DoesNotContain("plain-secret-value", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-secret-value", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret-value", persistedSegment.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-secret-value", persistedSegment.Text, StringComparison.Ordinal);
        Assert.All(persistedSegment.FunctionCall!.Arguments!.Values,
            value => Assert.Equal("[REDACTED]", value?.ToString()));
        Assert.Equal("plain-secret-value", segment.FunctionCall!.Arguments!["apiKey"]);
        Assert.Equal("stored-secret-value", segment.FunctionCall.Arguments["note"]);
    }

    [Fact]
    public async Task SaveThreadAsync_AppendsOnlyNewTranscriptLines()
    {
        var thread = new ChatThread { AgentId = "agent-1" };
        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "one" });
        await _repository.SaveThreadAsync("user-1", thread);

        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.Bot, Text = "two" });
        await _repository.SaveThreadAsync("user-1", thread);

        var lines = await File.ReadAllLinesAsync(GetTranscriptPath("user-1", thread.Id));
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task SaveThreadAsync_WhenTranscriptRewriteIsRequired_BackfillsBotAgentId()
    {
        var thread = new ChatThread
        {
            AgentId = "agent-1",
            DisplayMessages =
            [
                new ChatDisplayMessage { From = MessageOwner.User, Text = "remove me" },
                new ChatDisplayMessage { From = MessageOwner.Bot, Text = "keep me" }
            ]
        };
        await _repository.SaveThreadAsync("user-1", thread);
        thread.DisplayMessages.RemoveAt(0);
        thread.DisplayMessages[0].AgentId = null;

        await _repository.SaveThreadAsync("user-1", thread);

        var reloaded = await _repository.GetThreadAsync("user-1", thread.Id);
        Assert.Equal("agent-1", Assert.Single(reloaded!.DisplayMessages).AgentId);
    }

    [Fact]
    public async Task SaveThreadAsync_WithInstructionCapturedAfterInitialSave_UpdatesTranscriptHeader()
    {
        var thread = new ChatThread { AgentId = "agent-1" };
        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "one" });
        await _repository.SaveThreadAsync("user-1", thread);

        thread.SystemInstruction = "Use stored-secret-value carefully.";
        await _repository.SaveThreadAsync("user-1", thread);

        var result = await _repository.GetThreadAsync("user-1", thread.Id);

        Assert.NotNull(result);
        Assert.Equal("Use [REDACTED] carefully.", result!.SystemInstruction);
        var lines = await File.ReadAllLinesAsync(GetTranscriptPath("user-1", thread.Id));
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task SaveSessionElementAsync_WritesSessionSidecarWithoutTranscriptRewrite()
    {
        var thread = new ChatThread { AgentId = "agent-1" };
        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "one" });
        await _repository.SaveThreadAsync("user-1", thread);
        var transcriptPath = GetTranscriptPath("user-1", thread.Id);
        var before = File.GetLastWriteTimeUtc(transcriptPath);
        await Task.Delay(10);
        var session = JsonDocument.Parse("{\"fresh\":true}").RootElement.Clone();

        await _repository.SaveSessionElementAsync("user-1", thread.Id, session, thread.AgentId);

        Assert.Equal(before, File.GetLastWriteTimeUtc(transcriptPath));
        var loaded = await _repository.GetSessionElementAsync("user-1", thread.Id);
        Assert.True(loaded.HasValue);
        Assert.True(loaded.Value.GetProperty("fresh").GetBoolean());
    }

    [Fact]
    public async Task SaveThreadAsync_WithDeletedEncryptedThread_DoesNotRecreateIt()
    {
        var thread = new ChatThread { AgentId = "agent-1" };
        await _repository.SaveThreadAsync("user-1", thread);
        thread.IsEncrypted = true;
        await _repository.DeleteThreadAsync("user-1", thread.Id);

        await _repository.SaveThreadAsync("user-1", thread);

        Assert.Null(await _repository.GetThreadAsync("user-1", thread.Id));
        Assert.False(File.Exists(GetTranscriptPath("user-1", thread.Id)));
    }

    [Fact]
    public async Task SaveSessionElementAsync_AfterThreadDeletion_DoesNotRecreateIt()
    {
        var thread = new ChatThread { AgentId = "agent-1" };
        await _repository.SaveThreadAsync("user-1", thread);
        await _repository.DeleteThreadAsync("user-1", thread.Id);
        var session = JsonDocument.Parse("{\"stale\":true}").RootElement.Clone();

        await _repository.SaveSessionElementAsync("user-1", thread.Id, session, thread.AgentId);

        Assert.Null(await _repository.GetThreadAsync("user-1", thread.Id));
        Assert.False(File.Exists(GetSessionPath("user-1", thread.Id)));
    }

    [Fact]
    public async Task DeleteSessionElementAsync_RemovesOnlySessionSidecar()
    {
        var session = JsonDocument.Parse("{\"key\":\"value\"}").RootElement.Clone();
        var thread = new ChatThread { Title = "Keep Thread", AgentId = "agent-1", SessionElement = session };
        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "keep transcript" });
        await _repository.SaveThreadAsync("user-1", thread);

        await _repository.DeleteSessionElementAsync("user-1", thread.Id);

        var reloaded = await _repository.GetThreadAsync("user-1", thread.Id);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.SessionElement);
        Assert.Equal("keep transcript", Assert.Single(reloaded.DisplayMessages).Text);
        Assert.True(File.Exists(GetTranscriptPath("user-1", thread.Id)));
        Assert.False(File.Exists(GetSessionPath("user-1", thread.Id)));
    }

    [Fact]
    public async Task SaveThreadAsync_WhenSessionIsCleared_RemovesStaleSessionSidecar()
    {
        var thread = new ChatThread
        {
            AgentId = "agent-1",
            SessionElement = JsonSerializer.SerializeToElement(new { value = "stale" })
        };
        await _repository.SaveThreadAsync("user-1", thread);
        var sessionPath = GetSessionPath("user-1", thread.Id);
        Assert.True(File.Exists(sessionPath));

        thread.SessionElement = null;
        await _repository.SaveThreadAsync("user-1", thread);

        Assert.False(File.Exists(sessionPath));
        Assert.Null((await _repository.GetThreadAsync("user-1", thread.Id))!.SessionElement);
    }

    [Fact]
    public async Task UpdateWatermarksAsync_UpdatesIndexWithoutTranscriptRewrite()
    {
        var thread = new ChatThread { AgentId = "agent-1" };
        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "one" });
        await _repository.SaveThreadAsync("user-1", thread);
        var transcriptPath = GetTranscriptPath("user-1", thread.Id);
        var before = File.GetLastWriteTimeUtc(transcriptPath);
        await Task.Delay(10);

        await _repository.UpdateWatermarksAsync("user-1", thread.Id, lastIndexedMessageCount: 5, lastSummarizedMessageCount: 3);

        Assert.Equal(before, File.GetLastWriteTimeUtc(transcriptPath));
        var result = await _repository.GetThreadAsync("user-1", thread.Id);
        Assert.NotNull(result);
        Assert.Equal(5, result!.LastIndexedMessageCount);
        Assert.Equal(3, result.LastSummarizedMessageCount);
    }

    [Fact]
    public async Task SaveThreadAsync_AfterConcurrentWatermarkUpdate_PreservesMonotonicProgress()
    {
        var thread = new ChatThread { AgentId = "agent-1" };
        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "one" });
        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.Bot, Text = "two" });
        await _repository.SaveThreadAsync("user-1", thread);
        await _repository.UpdateWatermarksAsync(
            "user-1",
            thread.Id,
            lastIndexedMessageCount: 2,
            lastSummarizedMessageCount: 2,
            lastReflectedMessageCount: 2);

        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "three" });
        await _repository.SaveThreadAsync("user-1", thread);

        var result = await _repository.GetThreadAsync("user-1", thread.Id);
        Assert.NotNull(result);
        Assert.Equal(2, result!.LastIndexedMessageCount);
        Assert.Equal(2, result.LastSummarizedMessageCount);
        Assert.Equal(2, result.LastReflectedMessageCount);
    }

    [Fact]
    public async Task SaveThreadAsync_AfterBackgroundWatermarkUpdate_PreservesMonotonicProgress()
    {
        var thread = new ChatThread
        {
            AgentId = "agent-1",
            DisplayMessages =
            [
                new ChatDisplayMessage { From = MessageOwner.User, Text = "one" },
                new ChatDisplayMessage { From = MessageOwner.Bot, Text = "two" }
            ]
        };
        await _repository.SaveThreadAsync("user-1", thread);
        await _repository.UpdateWatermarksAsync(
            "user-1",
            thread.Id,
            lastIndexedMessageCount: 2,
            lastSummarizedMessageCount: 2);

        thread.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "three" });
        await _repository.SaveThreadAsync("user-1", thread);

        var result = await _repository.GetThreadAsync("user-1", thread.Id);
        Assert.NotNull(result);
        Assert.Equal(2, result!.LastIndexedMessageCount);
        Assert.Equal(2, result.LastSummarizedMessageCount);
    }

    [Fact]
    public async Task SaveThreadAsync_AfterExplicitIndexReset_DoesNotRestoreStaleProgress()
    {
        var thread = new ChatThread
        {
            AgentId = "agent-1",
            DisplayMessages =
            [
                new ChatDisplayMessage { From = MessageOwner.User, Text = "one" },
                new ChatDisplayMessage { From = MessageOwner.Bot, Text = "two" }
            ]
        };
        await _repository.SaveThreadAsync("user-1", thread);
        await _repository.UpdateWatermarksAsync(
            "user-1",
            thread.Id,
            lastIndexedMessageCount: 2,
            lastSummarizedMessageCount: null);
        var staleThread = await _repository.GetThreadAsync("user-1", thread.Id);
        Assert.NotNull(staleThread);
        await _repository.UpdateWatermarksAsync(
            "user-1",
            thread.Id,
            lastIndexedMessageCount: 0,
            lastSummarizedMessageCount: null);

        staleThread!.DisplayMessages.Add(new ChatDisplayMessage { From = MessageOwner.User, Text = "three" });
        await _repository.SaveThreadAsync("user-1", staleThread);

        var result = await _repository.GetThreadAsync("user-1", thread.Id);
        Assert.NotNull(result);
        Assert.Equal(0, result!.LastIndexedMessageCount);
    }

    [Fact]
    public async Task SaveThreadAsync_WithSetCurrentFalse_DoesNotClearExistingCurrentThread()
    {
        var currentThread = new ChatThread { Title = "Current", AgentId = "agent-1" };
        await _repository.SaveThreadAsync("user-1", currentThread);

        var notificationThread = new ChatThread { Title = "Notification", AgentId = "agent-1", IsUnread = true };
        await _repository.SaveThreadAsync("user-1", notificationThread, setCurrent: false);

        var threads = await _repository.GetThreadsAsync("user-1");

        Assert.True(threads.Single(thread => thread.Id == currentThread.Id).IsCurrent);
        Assert.False(threads.Single(thread => thread.Id == notificationThread.Id).IsCurrent);
    }

    [Fact]
    public async Task SaveThreadAsync_WithExistingCurrentThread_PromotesTarget()
    {
        var currentThread = new ChatThread { Title = "Current", AgentId = "agent-1" };
        var notificationThread = new ChatThread { Title = "Notification", AgentId = "agent-1", IsUnread = true };
        await _repository.SaveThreadAsync("user-1", currentThread);
        await _repository.SaveThreadAsync("user-1", notificationThread, setCurrent: false);

        notificationThread.IsUnread = false;
        await _repository.SaveThreadAsync("user-1", notificationThread);

        var threads = await _repository.GetThreadsAsync("user-1");
        Assert.False(threads.Single(thread => thread.Id == currentThread.Id).IsCurrent);
        Assert.True(threads.Single(thread => thread.Id == notificationThread.Id).IsCurrent);
        Assert.False(threads.Single(thread => thread.Id == notificationThread.Id).IsUnread);
    }

    [Fact]
    public async Task DeleteThreadAsync_WithExistingThread_RemovesIndexTranscriptAndSession()
    {
        var session = JsonDocument.Parse("{\"key\":\"value\"}").RootElement.Clone();
        var thread = new ChatThread { Title = "To Delete", SessionElement = session };
        thread.DisplayMessages.Add(new ChatDisplayMessage { Text = "hello" });
        await _repository.SaveThreadAsync("user-1", thread);

        await _repository.DeleteThreadAsync("user-1", thread.Id);

        Assert.Empty(await _repository.GetThreadsAsync("user-1"));
        Assert.False(File.Exists(GetTranscriptPath("user-1", thread.Id)));
        Assert.False(File.Exists(GetSessionPath("user-1", thread.Id)));
    }

    [Fact]
    public async Task DeleteThreadAsync_WhenSidecarDeletionFails_PreservesVisibleThreadAndReportsFailure()
    {
        var session = JsonSerializer.SerializeToElement(new { value = "session" });
        var thread = new ChatThread { Title = "Keep On Failure", SessionElement = session };
        await _repository.SaveThreadAsync("user-1", thread);
        var transcriptPath = GetTranscriptPath("user-1", thread.Id);
        File.Delete(transcriptPath);
        Directory.CreateDirectory(transcriptPath);

        var exception = await Record.ExceptionAsync(() => _repository.DeleteThreadAsync("user-1", thread.Id));

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Equal(thread.Id, Assert.Single(await _repository.GetThreadsAsync("user-1")).Id);
        Assert.True(Directory.Exists(transcriptPath));
        Assert.True(File.Exists(GetSessionPath("user-1", thread.Id)));
    }

    [Fact]
    public async Task DeleteAllThreadsAsync_DoesNotAffectOtherUsers()
    {
        await _repository.SaveThreadAsync("user-1", new ChatThread { Title = "User 1" });
        await _repository.SaveThreadAsync("user-2", new ChatThread { Title = "User 2" });

        var deleted = await _repository.DeleteAllThreadsAsync("user-1");

        Assert.Equal(1, deleted);
        Assert.Empty(await _repository.GetThreadsAsync("user-1"));
        Assert.Single(await _repository.GetThreadsAsync("user-2"));
    }

    [Fact]
    public async Task GetThreadsAsync_SkipsMalformedIndexRows()
    {
        var thread = new ChatThread { Title = "Valid" };
        await _repository.SaveThreadAsync("user-1", thread);
        await File.AppendAllTextAsync(GetIndexPath("user-1"), "not json" + Environment.NewLine);

        var result = await _repository.GetThreadsAsync("user-1");

        var loaded = Assert.Single(result);
        Assert.Equal(thread.Id, loaded.Id);
    }

    [Fact]
    public async Task SaveThreadAsync_WithIncognitoThread_WritesNothingToDiskAndStaysOutOfTheThreadList()
    {
        var persisted = new ChatThread { Title = "Persisted" };
        await _repository.SaveThreadAsync("user-1", persisted);
        var indexBefore = await File.ReadAllTextAsync(GetIndexPath("user-1"));

        var incognito = new ChatThread { Title = "Incognito", IsIncognito = true };
        incognito.DisplayMessages.Add(new ChatDisplayMessage { Text = "hello" });
        await _repository.SaveThreadAsync("user-1", incognito);

        Assert.Equal(indexBefore, await File.ReadAllTextAsync(GetIndexPath("user-1")));
        Assert.False(File.Exists(GetTranscriptPath("user-1", incognito.Id)));
        Assert.False(File.Exists(GetSessionPath("user-1", incognito.Id)));

        var threads = await _repository.GetThreadsAsync("user-1");
        Assert.Equal(persisted.Id, Assert.Single(threads).Id);

        var loaded = await _repository.GetThreadAsync("user-1", incognito.Id);
        Assert.Same(incognito, loaded);
        Assert.Equal(incognito.Id, Assert.Single(await _repository.GetIncognitoThreadsAsync("user-1")).Id);
    }

    [Fact]
    public async Task SaveThreadAsync_WithIncognitoThread_DoesNotBecomeCurrent()
    {
        var persisted = new ChatThread { Title = "Persisted", AgentId = "agent-1" };
        await _repository.SaveThreadAsync("user-1", persisted);

        var incognito = new ChatThread { Title = "Incognito", AgentId = "agent-1", IsIncognito = true, IsCurrent = true };
        await _repository.SaveThreadAsync("user-1", incognito, setCurrent: true);

        Assert.False(incognito.IsCurrent);
        Assert.True(Assert.Single(await _repository.GetThreadsAsync("user-1")).IsCurrent);
    }

    [Fact]
    public async Task DeleteThreadAsync_WithIncognitoThread_RemovesItFromMemory()
    {
        var incognito = new ChatThread { Title = "Incognito", IsIncognito = true };
        await _repository.SaveThreadAsync("user-1", incognito);

        await _repository.DeleteThreadAsync("user-1", incognito.Id);

        Assert.Empty(await _repository.GetIncognitoThreadsAsync("user-1"));
        Assert.Null(await _repository.GetThreadAsync("user-1", incognito.Id));
    }

    [Fact]
    public async Task DeleteAllThreadsAsync_WithIncognitoThreads_ClearsOnlyTheRequestedUser()
    {
        await _repository.SaveThreadAsync("user-1", new ChatThread { Title = "Persisted" });
        await _repository.SaveThreadAsync("user-1", new ChatThread { Title = "Incognito", IsIncognito = true });
        await _repository.SaveThreadAsync("user-2", new ChatThread { Title = "Other", IsIncognito = true });

        var deleted = await _repository.DeleteAllThreadsAsync("user-1");

        Assert.Equal(2, deleted);
        Assert.Empty(await _repository.GetIncognitoThreadsAsync("user-1"));
        Assert.Single(await _repository.GetIncognitoThreadsAsync("user-2"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_usersRoot))
        {
            Directory.Delete(_usersRoot, recursive: true);
        }
    }

    private string GetIndexPath(string userId)
    {
        return Path.Combine(_usersRoot, userId, "chat", "index.jsonl");
    }

    private string GetTranscriptPath(string userId, string threadId)
    {
        return Path.Combine(_usersRoot, userId, "chat", threadId + ".jsonl");
    }

    private string GetSessionPath(string userId, string threadId)
    {
        return Path.Combine(_usersRoot, userId, "chat", threadId + ".session.json");
    }

    private ChatThreadRepository CreateRepository(
        PassThroughContentProtector protector,
        InMemoryAgentDefinitionRepository agents) => new(
            () => _usersRoot,
            new FileSessionIndexCoordinator(),
            new SecretRedactionService(),
            protector,
            agents,
            NullLogger<ChatThreadRepository>.Instance);

    private sealed class SecretRedactionService : IRedactionService
    {
        public Task<string> RedactTextAsync(string text, string? userId = null, CancellationToken ct = default)
        {
            return Task.FromResult(text
                .Replace("plain-secret-value", "[REDACTED]", StringComparison.Ordinal)
                .Replace("stored-secret-value", "[REDACTED]", StringComparison.Ordinal));
        }

        public Task<IDictionary<string, object?>> RedactArgumentsAsync(
            IDictionary<string, object?> arguments,
            string? userId = null,
            CancellationToken ct = default)
        {
            return Task.FromResult<IDictionary<string, object?>>(arguments.ToDictionary(
                static pair => pair.Key,
                static _ => (object?)"[REDACTED]",
                StringComparer.Ordinal));
        }

        public Task<IReadOnlyList<ChatMessage>> RedactMessagesAsync(
            IReadOnlyList<ChatMessage> messages,
            string? userId = null,
            CancellationToken ct = default)
        {
            return Task.FromResult(messages);
        }
    }
}
