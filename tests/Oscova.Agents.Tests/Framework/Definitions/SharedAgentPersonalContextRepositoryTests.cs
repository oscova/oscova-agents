using System.Reflection;
using Oscova.Agents.Persistence;

namespace Oscova.Agents.Tests.Framework.Definitions;

public sealed class SharedAgentPersonalContextRepositoryTests : IDisposable
{
    private const long MaxTextSizeBytes = 512 * 1024;
    private readonly string _contextRoot = Path.Combine(
        Path.GetTempPath(),
        $"oscova-shared-agent-context-{Guid.NewGuid():N}");
    private readonly string _outsideLinkTarget = Path.Combine(
        Path.GetTempPath(),
        $"oscova-shared-agent-context-outside-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_contextRoot))
        {
            Directory.Delete(_contextRoot, recursive: true);
        }

        if (Directory.Exists(_outsideLinkTarget))
        {
            Directory.Delete(_outsideLinkTarget, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_WhenNoContextSaved_ReturnsEmpty()
    {
        var result = await CreateRepository().GetAsync("user-1", SharedAgent("Helper"));

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task SaveAsync_ThenGetAsync_ReturnsBody()
    {
        var repository = CreateRepository();
        var agent = SharedAgent("Researcher");

        await repository.SaveAsync("user-1", agent, "my context");
        var result = await repository.GetAsync("user-1", agent);

        Assert.Equal("my context", result);
    }

    [Fact]
    public async Task SaveAsync_CreatesFileUnderAgentsSharedSlug()
    {
        var repository = CreateRepository();
        var agent = SharedAgent("Researcher");

        await repository.SaveAsync("user-1", agent, "body");

        var contextPath = Path.Combine(_contextRoot, "researcher", "context.md");
        Assert.True(File.Exists(contextPath));
    }

    [Fact]
    public async Task SaveAsync_CalledTwice_ReusesExistingFolder()
    {
        var repository = CreateRepository();
        var agent = SharedAgent("Researcher");

        await repository.SaveAsync("user-1", agent, "first");
        await repository.SaveAsync("user-1", agent, "second");

        var folders = Directory.GetDirectories(_contextRoot);
        Assert.Single(folders);
        Assert.Equal("second", await repository.GetAsync("user-1", agent));
    }

    [Fact]
    public async Task GetAsync_FindsFileByAgentId_AfterAgentRename()
    {
        var repository = CreateRepository();
        var agentId = Guid.NewGuid().ToString();
        var original = SharedAgentWithId(agentId, "Researcher");

        await repository.SaveAsync("user-1", original, "context");

        // Simulate admin renaming the agent — same id, different name.
        var renamed = SharedAgentWithId(agentId, "Senior Researcher");
        var result = await repository.GetAsync("user-1", renamed);

        Assert.Equal("context", result);
    }

    [Fact]
    public async Task GetAsync_WhenEarlierContextHasUnterminatedFrontmatter_FindsValidLaterFolder()
    {
        var repository = CreateRepository();
        var agent = SharedAgent("Researcher");
        await repository.SaveAsync("user-1", agent, "valid context");
        var malformedPath = Path.Combine(_contextRoot, "aaa-malformed", "context.md");
        Directory.CreateDirectory(Path.GetDirectoryName(malformedPath)!);
        await File.WriteAllTextAsync(malformedPath, "---\nagent_id: malformed");

        var result = await repository.GetAsync("user-1", agent);

        Assert.Equal("valid context", result);
    }

    [Fact]
    public async Task SaveAsync_TwoAgentsWithSameName_GetDistinctFolders()
    {
        var repository = CreateRepository();
        var agentA = SharedAgent("Helper");
        var agentB = SharedAgent("Helper");

        await repository.SaveAsync("user-1", agentA, "context A");
        await repository.SaveAsync("user-1", agentB, "context B");

        Assert.Equal("context A", await repository.GetAsync("user-1", agentA));
        Assert.Equal("context B", await repository.GetAsync("user-1", agentB));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentAgentsWithSameName_GetDistinctFolders()
    {
        using var startBarrier = new Barrier(2);
        var resolveCount = 0;
        var repository = new SharedAgentPersonalContextRepository(
            _ =>
            {
                if (Interlocked.Increment(ref resolveCount) <= 2)
                {
                    startBarrier.SignalAndWait();
                }

                return _contextRoot;
            },
            MaxTextSizeBytes,
            new YamlConverter());
        var agentA = SharedAgent("Helper");
        var agentB = SharedAgent("Helper");

        Task saveA = Task.Run(() => repository.SaveAsync("user-1", agentA, "context A"));
        Task saveB = Task.Run(() => repository.SaveAsync("user-1", agentB, "context B"));
        await Task.WhenAll(saveA, saveB);

        Assert.Equal(2, Directory.GetDirectories(_contextRoot).Length);
        Assert.Equal("context A", await repository.GetAsync("user-1", agentA));
        Assert.Equal("context B", await repository.GetAsync("user-1", agentB));
    }

    [Fact]
    public async Task SaveAsync_WhenCancelledBeforeGateAdmission_DoesNotWrite()
    {
        var gateField = typeof(SharedAgentPersonalContextRepository).GetField(
            "SaveGate",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Personal-context save gate was not found.");
        var saveGate = (SemaphoreSlim)gateField.GetValue(null)!;
        await saveGate.WaitAsync();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var save = CreateRepository().SaveAsync(
                "user-1",
                SharedAgent("Researcher"),
                "must not be written",
                cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => save.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(Directory.Exists(_contextRoot));
        }
        finally
        {
            _ = saveGate.Release();
        }
    }

    [Fact]
    public async Task SaveAsync_WhenContentExceedsLimit_DoesNotCreateContextFile()
    {
        var repository = CreateRepository(maxBytes: 32);
        var agent = SharedAgent("Researcher");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.SaveAsync("user-1", agent, new string('x', 33)));

        Assert.False(File.Exists(Path.Combine(_contextRoot, "researcher", "context.md")));
    }

    [Fact]
    public async Task GetAsync_WhenContextFileExceedsLimit_Throws()
    {
        var agent = SharedAgent("Researcher");
        var contextPath = Path.Combine(_contextRoot, "researcher", "context.md");
        Directory.CreateDirectory(Path.GetDirectoryName(contextPath)!);
        await File.WriteAllTextAsync(
            contextPath,
            $"---\nagent_id: {agent.Id}\n---\n{new string('x', 33)}");
        var repository = CreateRepository(maxBytes: 32);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.GetAsync("user-1", agent));
    }

    [SkippableFact]
    public async Task SaveAsync_WhenSlugDirectoryIsLinked_RejectsEscape()
    {
        var linkedSlugPath = Path.Combine(_contextRoot, "researcher");
        Directory.CreateDirectory(_contextRoot);
        Directory.CreateDirectory(_outsideLinkTarget);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkedSlugPath, _outsideLinkTarget);
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            {
                Skip.If(true, $"Symbolic link creation is unavailable on this runner: {exception.Message}");
            }

            var repository = CreateRepository();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                repository.SaveAsync("user-1", SharedAgent("Researcher"), "must stay contained"));
            Assert.False(File.Exists(Path.Combine(_outsideLinkTarget, "context.md")));
        }
        finally
        {
            if (Directory.Exists(linkedSlugPath))
            {
                Directory.Delete(linkedSlugPath);
            }
        }
    }

    [Fact]
    public async Task GetAsync_WhenRootChanges_UsesCurrentRoot()
    {
        var firstRoot = Path.Combine(_contextRoot, "first");
        var secondRoot = Path.Combine(_contextRoot, "second");
        var currentRoot = firstRoot;
        var repository = new SharedAgentPersonalContextRepository(
            _ => currentRoot,
            MaxTextSizeBytes,
            new YamlConverter());
        var agent = SharedAgent("Researcher");
        await repository.SaveAsync("user-1", agent, "first root");

        currentRoot = secondRoot;

        Assert.Empty(await repository.GetAsync("user-1", agent));
    }

    [Fact]
    public async Task DeleteForUsersAsync_WithDuplicateCaseVariantUserIds_DeletesOnce()
    {
        var repository = CreateRepository();
        var agent = SharedAgent("Helper");
        await repository.SaveAsync("User-1", agent, "context");

        var deletedCount = await repository.DeleteForUsersAsync(["User-1", "user-1"], agent);

        Assert.Equal(1, deletedCount);
        Assert.False(File.Exists(Path.Combine(_contextRoot, "helper", "context.md")));
    }

    private SharedAgentPersonalContextRepository CreateRepository(long maxBytes = MaxTextSizeBytes) =>
        new(_ => _contextRoot, maxBytes, new YamlConverter());

    private static AgentDefinition SharedAgent(string name) => SharedAgentWithId(Guid.NewGuid().ToString(), name);

    private static AgentDefinition SharedAgentWithId(string id, string name) => new()
    {
        Id = id,
        Name = name,
        IsShared = true
    };
}
