using Oscova.Agents.Execution;
using Microsoft.Agents.AI.Tools.Shell;
using Oscova.Agents.Collections;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;
using Oscova.Agents.Toolsets;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class ShellToolsetTests
{
    [Fact]
    public async Task ExecCommand_TrimsLongStdoutAndSavesIt()
    {
        string stdout = new string('h', 10_000) + new string('t', 10_000);
        var store = new RecordingToolOutputStore();
        var toolset = new ShellToolset(
            new FakeShellExecutorProvider(new FakeShellExecutor(new ShellResult(stdout, string.Empty, 0, TimeSpan.Zero))),
            new FakeShellBackgroundJobProvider(), store);

        object result = await toolset.ExecCommandAsync("echo long");
        string inline = GetProperty<string>(result, "stdout");

        Assert.StartsWith(new string('h', 8_000), inline);
        Assert.EndsWith(new string('t', 8_000), inline);
        Assert.Contains("Full output saved", inline);
        Assert.Equal(stdout, store.Saved);
    }

    [Fact]
    public async Task ExecCommand_WhenExecutorTruncated_SavesCapturedOutputOnly()
    {
        string captured = new string('h', 500_000) + "[... truncated 5000000 bytes ...]" + new string('t', 500_000);
        var store = new RecordingToolOutputStore();
        var toolset = new ShellToolset(
            new FakeShellExecutorProvider(new FakeShellExecutor(new ShellResult(captured, string.Empty, 0, TimeSpan.Zero, Truncated: true))),
            new FakeShellBackgroundJobProvider(), store);

        object result = await toolset.ExecCommandAsync("echo huge");
        string inline = GetProperty<string>(result, "stdout");

        Assert.StartsWith(new string('h', 8_000), inline);
        Assert.EndsWith(new string('t', 8_000), inline);
        Assert.Contains("captured characters", inline);
        Assert.Contains("The shell may have dropped part of the original output", inline);
        Assert.DoesNotContain("Full output saved", inline);
        Assert.Equal(captured, store.Saved);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(store.Saved!) <= BoundedOutputBuffer.DefaultCapacityBytes);
    }

    [Fact]
    public async Task ExecCommandAsync_DelegatesToConfiguredExecutor()
    {
        var executor = new FakeShellExecutor(new ShellResult("stdout", "stderr", 7, TimeSpan.FromMilliseconds(5), Truncated: true));
        var provider = new FakeShellExecutorProvider(executor);
        var jobs = new FakeShellBackgroundJobProvider();
        var toolset = new ShellToolset(provider, jobs);

        object result = await toolset.ExecCommandAsync("echo hi");

        Assert.Equal("echo hi", executor.Command);
        Assert.Equal(7, GetProperty<int>(result, "exitCode"));
        Assert.Equal("stdout", GetProperty<string>(result, "stdout"));
        Assert.Equal("stderr", GetProperty<string>(result, "stderr"));
        Assert.False(GetProperty<bool>(result, "timedOut"));
        Assert.True(GetProperty<bool>(result, "truncated"));
    }

    [Fact]
    public async Task ExecCommandAsync_WhenExecutorPolicyRejectsCommand_ReturnsBoundedToolFailure()
    {
        var toolset = new ShellToolset(
            new FakeShellExecutorProvider(new RejectingShellExecutor()),
            new FakeShellBackgroundJobProvider());

        var exception = await Assert.ThrowsAsync<ToolFailureException>(() =>
            toolset.ExecCommandAsync("harmless custom-policy command"));

        Assert.Equal("Shell command was rejected by the execution policy.", exception.Message);
        Assert.DoesNotContain("destructive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecCommandAsync_WithWhitespaceCommand_Throws()
    {
        var provider = new FakeShellExecutorProvider(new FakeShellExecutor(new ShellResult("", "", 0, TimeSpan.Zero)));
        var jobs = new FakeShellBackgroundJobProvider();
        var toolset = new ShellToolset(provider, jobs);

        await Assert.ThrowsAsync<ToolFailureException>(() => toolset.ExecCommandAsync("   "));
    }

    [Fact]
    public async Task ExecCommandAsync_WhenTimeoutElapses_Returns124AndResetsSession()
    {
        var provider = new FakeShellExecutorProvider(new BlockingShellExecutor());
        var jobs = new FakeShellBackgroundJobProvider();
        var toolset = new ShellToolset(provider, jobs);

        object result = await toolset.ExecCommandAsync("sleep forever", timeoutSeconds: 1);

        Assert.True(GetProperty<bool>(result, "timedOut"));
        Assert.Equal(124, GetProperty<int>(result, "exitCode"));
        Assert.True(provider.RecoverCalled);
    }

    [Fact]
    public async Task ExecCommandAsync_WhenCallerCancels_RecoversBeforePropagatingCancellation()
    {
        var executor = new BlockingShellExecutor();
        var provider = new FakeShellExecutorProvider(executor);
        var toolset = new ShellToolset(provider, new FakeShellBackgroundJobProvider());
        using var cancellation = new CancellationTokenSource();

        Task<object> running = toolset.ExecCommandAsync("sleep forever", timeoutSeconds: 600, cancellationToken: cancellation.Token);
        await executor.Started;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(provider.RecoverCalled);
    }

    [Fact]
    public async Task ExecHostCommandAsync_DelegatesToOneShotExecutorAndDisposesIt()
    {
        var executor = new FakeShellExecutor(new ShellResult("host stdout", "host stderr", 3, TimeSpan.Zero, Truncated: true));
        var toolset = new HostShellToolset(_ => Task.FromResult<ShellExecutor>(executor));

        object result = await toolset.ExecHostCommandAsync("host command");

        Assert.Equal("host command", executor.Command);
        Assert.Equal(3, GetProperty<int>(result, "exitCode"));
        Assert.Equal("host stdout", GetProperty<string>(result, "stdout"));
        Assert.Equal("host stderr", GetProperty<string>(result, "stderr"));
        Assert.True(GetProperty<bool>(result, "truncated"));
        Assert.Equal(1, executor.DisposeCount);
    }

    [Fact]
    public async Task ExecHostCommandAsync_WhenTimeoutElapses_DisposesExecutorExactlyOnce()
    {
        var executor = new BlockingShellExecutor();
        var toolset = new HostShellToolset(_ => Task.FromResult<ShellExecutor>(executor));

        object result = await toolset.ExecHostCommandAsync("sleep forever", timeoutSeconds: 1);

        Assert.True(GetProperty<bool>(result, "timedOut"));
        Assert.Equal(124, GetProperty<int>(result, "exitCode"));
        Assert.Equal(1, executor.DisposeCount);
    }

    [Fact]
    public async Task StartCommandAsync_DelegatesToProviderAndMapsResult()
    {
        var jobs = new FakeShellBackgroundJobProvider
        {
            StartResult = new StartJobResult(true, "job-1", "started")
        };
        var toolset = new ShellToolset(CreateUnusedExecutorProvider(), jobs);

        object result = await toolset.StartCommandAsync("dotnet watch");

        Assert.Equal("dotnet watch", jobs.StartCommand);
        Assert.True(GetProperty<bool>(result, "started"));
        Assert.Equal("job-1", GetProperty<string>(result, "jobId"));
        Assert.Equal("started", GetProperty<string>(result, "message"));
    }

    [Fact]
    public async Task ReadCommandOutputAsync_DelegatesToProviderAndMapsResult()
    {
        var jobs = new FakeShellBackgroundJobProvider
        {
            ReadResult = new ReadJobResult(true, "line", 4, 10, "running", null, true, "ok")
        };
        var toolset = new ShellToolset(CreateUnusedExecutorProvider(), jobs);

        object result = await toolset.ReadCommandOutputAsync("job-1", afterOffset: 2, maxBytes: 8);

        Assert.Equal("job-1", jobs.ReadJobId);
        Assert.Equal(2, jobs.ReadAfterOffset);
        Assert.Equal(8, jobs.ReadMaxBytes);
        Assert.True(GetProperty<bool>(result, "found"));
        Assert.Equal("line", GetProperty<string>(result, "output"));
        Assert.Equal(4, GetProperty<long>(result, "newOffset"));
        Assert.Equal(10, GetProperty<long>(result, "totalSize"));
        Assert.Equal("running", GetProperty<string>(result, "status"));
        Assert.Null(result.GetType().GetProperty("exitCode")?.GetValue(result));
        Assert.True(GetProperty<bool>(result, "truncated"));
        Assert.Equal("ok", GetProperty<string>(result, "message"));
    }

    [Fact]
    public async Task ReadCommandOutputAsync_WithNegativeOffset_ClampsToZero()
    {
        var jobs = new FakeShellBackgroundJobProvider
        {
            ReadResult = new ReadJobResult(true, string.Empty, 0, 0, "running", null, false, "ok")
        };
        var toolset = new ShellToolset(CreateUnusedExecutorProvider(), jobs);

        await toolset.ReadCommandOutputAsync("job-1", afterOffset: -42);

        Assert.Equal(0, jobs.ReadAfterOffset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadCommandOutputAsync_WithNonPositiveMaxBytes_UsesProviderDefault(int maxBytes)
    {
        var jobs = new FakeShellBackgroundJobProvider
        {
            ReadResult = new ReadJobResult(true, string.Empty, 0, 0, "running", null, false, "ok")
        };
        var toolset = new ShellToolset(CreateUnusedExecutorProvider(), jobs);

        await toolset.ReadCommandOutputAsync("job-1", maxBytes: maxBytes);

        Assert.Null(jobs.ReadMaxBytes);
    }

    [Fact]
    public async Task KillCommandAsync_DelegatesToProviderAndMapsResult()
    {
        var jobs = new FakeShellBackgroundJobProvider
        {
            KillResult = new KillJobResult(true, true, "stopped")
        };
        var toolset = new ShellToolset(CreateUnusedExecutorProvider(), jobs);

        object result = await toolset.KillCommandAsync("job-1");

        Assert.Equal("job-1", jobs.KillJobId);
        Assert.True(GetProperty<bool>(result, "found"));
        Assert.True(GetProperty<bool>(result, "killed"));
        Assert.Equal("stopped", GetProperty<string>(result, "message"));
    }

    [Fact]
    public async Task KillCommandAsync_WhenJobAlreadyStopped_ReturnsExistingResult()
    {
        var jobs = new FakeShellBackgroundJobProvider
        {
            KillResult = new KillJobResult(true, false, "already stopped")
        };
        var toolset = new ShellToolset(CreateUnusedExecutorProvider(), jobs);

        object result = await toolset.KillCommandAsync("job-1");

        Assert.True(GetProperty<bool>(result, "found"));
        Assert.False(GetProperty<bool>(result, "killed"));
        Assert.Equal("already stopped", GetProperty<string>(result, "message"));
    }

    [Fact]
    public async Task BackgroundMethods_WhenOperationCannotStartOrFindJob_Throw()
    {
        var jobs = new FakeShellBackgroundJobProvider
        {
            StartResult = new StartJobResult(false, null, "provider detail"),
            ReadResult = new ReadJobResult(false, string.Empty, 0, 0, "missing", null, false, "provider detail"),
            KillResult = new KillJobResult(false, false, "provider detail")
        };
        var toolset = new ShellToolset(CreateUnusedExecutorProvider(), jobs);

        await Assert.ThrowsAsync<ToolFailureException>(() => toolset.StartCommandAsync("run"));
        await Assert.ThrowsAsync<ToolFailureException>(() => toolset.ReadCommandOutputAsync("missing"));
        await Assert.ThrowsAsync<ToolFailureException>(() => toolset.KillCommandAsync("missing"));
    }

    [Theory]
    [InlineData(nameof(ShellToolset.ExecCommandAsync))]
    [InlineData(nameof(ShellToolset.StartCommandAsync))]
    [InlineData(nameof(ShellToolset.ReadCommandOutputAsync))]
    [InlineData(nameof(ShellToolset.KillCommandAsync))]
    public async Task ToolMethods_WithWhitespaceRequiredInput_Throw(string methodName)
    {
        var toolset = new ShellToolset(CreateUnusedExecutorProvider(), new FakeShellBackgroundJobProvider());

        Task<object> call = methodName switch
        {
            nameof(ShellToolset.ExecCommandAsync) => toolset.ExecCommandAsync("   "),
            nameof(ShellToolset.StartCommandAsync) => toolset.StartCommandAsync("   "),
            nameof(ShellToolset.ReadCommandOutputAsync) => toolset.ReadCommandOutputAsync("   "),
            nameof(ShellToolset.KillCommandAsync) => toolset.KillCommandAsync("   "),
            _ => throw new UnreachableException()
        };

        await Assert.ThrowsAsync<ToolFailureException>(() => call);
    }

    [Fact]
    public void ToolMethods_HaveExpectedToolMetadata()
    {
        MethodInfo exec = GetMethod(nameof(ShellToolset.ExecCommandAsync));
        MethodInfo start = GetMethod(nameof(ShellToolset.StartCommandAsync));
        MethodInfo read = GetMethod(nameof(ShellToolset.ReadCommandOutputAsync));
        MethodInfo kill = GetMethod(nameof(ShellToolset.KillCommandAsync));

        Assert.Equal("exec", exec.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName);
        Assert.Equal(ToolApprovalMode.RequireApproval, exec.GetCustomAttribute<ToolPolicyAttribute>()?.ApprovalMode);
        Assert.Equal(NetworkScope.Incidental, exec.GetCustomAttribute<ToolNetworkScopeAttribute>()?.Scope);

        Assert.Equal("start", start.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName);
        Assert.Equal(ToolApprovalMode.RequireApproval, start.GetCustomAttribute<ToolPolicyAttribute>()?.ApprovalMode);
        Assert.Equal(NetworkScope.Incidental, start.GetCustomAttribute<ToolNetworkScopeAttribute>()?.Scope);

        Assert.Equal("read_output", read.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName);
        Assert.Null(read.GetCustomAttribute<ToolPolicyAttribute>());
        Assert.Null(read.GetCustomAttribute<ToolNetworkScopeAttribute>());

        Assert.Equal("kill", kill.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName);
        Assert.Equal(ToolApprovalMode.RequireApproval, kill.GetCustomAttribute<ToolPolicyAttribute>()?.ApprovalMode);
        Assert.Null(kill.GetCustomAttribute<ToolNetworkScopeAttribute>());
    }

    [Fact]
    public void ToolMethods_AreReflectedUnderShellOwner()
    {
        var collection = new AgentToolCollection();
        var toolset = new ShellToolset(CreateUnusedExecutorProvider(), new FakeShellBackgroundJobProvider());

        collection.Add(toolset);

        Assert.NotNull(collection.Find("shell__exec"));
        Assert.NotNull(collection.Find("shell__start"));
        Assert.NotNull(collection.Find("shell__read_output"));
        Assert.NotNull(collection.Find("shell__kill"));
        Assert.Null(collection.Find("background_shell__start"));
    }

    [Fact]
    public void HostShellToolset_HasExpectedForegroundMetadata()
    {
        MethodInfo method = typeof(HostShellToolset).GetMethod(nameof(HostShellToolset.ExecHostCommandAsync))
            ?? throw new InvalidOperationException("Missing host command method.");
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var collection = new AgentToolCollection();

        collection.Add(new HostShellToolset(_ => throw new NotSupportedException()));

        Assert.Equal("host_shell__exec", HostShellToolset.QualifiedToolName);
        Assert.Single(collection);
        Assert.NotNull(collection.Find(HostShellToolset.QualifiedToolName));
        Assert.Equal(HostShellToolset.ToolName, method.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName);
        Assert.Equal(ToolApprovalMode.RequireApproval, method.GetCustomAttribute<ToolPolicyAttribute>()?.ApprovalMode);
        Assert.Equal(NetworkScope.Incidental, method.GetCustomAttribute<ToolNetworkScopeAttribute>()?.Scope);
        Assert.Contains("shell__exec", description, StringComparison.Ordinal);
        Assert.DoesNotContain("background", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(method.GetParameters(), parameter =>
            parameter.Name?.Contains("path", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Single(typeof(HostShellToolset).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
    }

    private static T GetProperty<T>(object result, string propertyName)
    {
        object? value = result.GetType().GetProperty(propertyName)?.GetValue(result);
        return Assert.IsType<T>(value);
    }

    private static MethodInfo GetMethod(string methodName) =>
        typeof(ShellToolset).GetMethod(methodName) ?? throw new InvalidOperationException($"Missing method {methodName}.");

    private static IShellExecutorProvider CreateUnusedExecutorProvider() =>
        new FakeShellExecutorProvider(new FakeShellExecutor(new ShellResult("", "", 0, TimeSpan.Zero)));

    private sealed class FakeShellExecutorProvider(ShellExecutor executor) : IShellExecutorProvider
    {
        public bool RecoverCalled { get; private set; }

        public Task<ShellExecutor> GetOrCreateAsync(CancellationToken cancellationToken = default) => Task.FromResult(executor);

        public Task<IExecutionSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> PrepareCommandAsync(string command, string? workdir, CancellationToken cancellationToken = default) => Task.FromResult(command);

        public Task EvictAsync(string executionId) => Task.CompletedTask;

        public Task RecoverForegroundCommandAsync()
        {
            RecoverCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShellExecutor(ShellResult result) : ShellExecutor
    {
        public string? Command { get; private set; }

        public int DisposeCount { get; private set; }

        public override Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
        {
            Command = command;
            return Task.FromResult(result);
        }

        public override Microsoft.Extensions.AI.AIFunction AsAIFunction(
            string name = "run_shell",
            string? description = null,
            bool requireApproval = true) => throw new NotSupportedException();

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RejectingShellExecutor : ShellExecutor
    {
        public override Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default) =>
            throw new ShellCommandRejectedException("custom allowlist rejection");

        public override Microsoft.Extensions.AI.AIFunction AsAIFunction(
            string name = "run_shell",
            string? description = null,
            bool requireApproval = true) => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeShellBackgroundJobProvider : IShellBackgroundJobProvider
    {
        public StartJobResult StartResult { get; set; } = new(false, null, "not started");

        public ReadJobResult ReadResult { get; set; } = new(false, string.Empty, 0, 0, "unknown", null, false, "not found");

        public KillJobResult KillResult { get; set; } = new(false, false, "not found");

        public string? StartCommand { get; private set; }

        public string? ReadJobId { get; private set; }

        public long? ReadAfterOffset { get; private set; }

        public int? ReadMaxBytes { get; private set; }

        public string? KillJobId { get; private set; }

        public Task<StartJobResult> StartAsync(string command, CancellationToken cancellationToken = default)
        {
            StartCommand = command;
            return Task.FromResult(StartResult);
        }

        public Task<ReadJobResult> ReadAsync(
            string jobId,
            long afterOffset,
            int? maxBytes,
            CancellationToken cancellationToken = default)
        {
            ReadJobId = jobId;
            ReadAfterOffset = afterOffset;
            ReadMaxBytes = maxBytes;
            return Task.FromResult(ReadResult);
        }

        public Task<KillJobResult> KillAsync(string jobId, CancellationToken cancellationToken = default)
        {
            KillJobId = jobId;
            return Task.FromResult(KillResult);
        }

        public Task CleanupExecutionAsync(string executionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CleanupUserAsync(string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool HasLiveJobs(string executionId) => false;
    }

    // Blocks until the supplied token cancels, mirroring a command that runs past its timeout.
    private sealed class BlockingShellExecutor : ShellExecutor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public int DisposeCount { get; private set; }

        public override async Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new ShellResult("", "", 0, TimeSpan.Zero);
        }

        public override Microsoft.Extensions.AI.AIFunction AsAIFunction(
            string name = "run_shell",
            string? description = null,
            bool requireApproval = true) => throw new NotSupportedException();

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
