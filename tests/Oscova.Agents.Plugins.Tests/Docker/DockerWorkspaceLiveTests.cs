using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Execution;
using Oscova.Agents.Plugins.Docker;
using Oscova.Agents.Workspace;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Plugins.Tests.Docker;

/// <summary>Checks effective directory grants in a real disposable Docker process.</summary>
public sealed class DockerWorkspaceLiveTests
{
    /// <summary>Discards a completed job's temporary output while retaining other jobs' output.</summary>
    [DockerFact]
    [Trait("Category", "Slow")]
    public async Task BackgroundJob_DisposeRemovesOnlyItsArtifacts()
    {
        var root = Directory.CreateTempSubdirectory("oscova-docker-cleanup-");
        try
        {
            var template = PluginTestContext.Create(new MissingCredentials(), root.FullName, root.FullName);
            var context = new AgentPluginContext(template.CredentialResolver, template.Services,
                NullLoggerFactory.Instance, TimeProvider.System, root.FullName, root.FullName)
            { InstallationSettings = new EmptySettings(), PrivateDataDirectory = root.FullName };
            await using var provider = new DockerExecutionProvider(context);
            IReadOnlyList<AllowedDirectory> grants =
                [new(root.FullName, "output", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/output", true)];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var session = await provider.CreateSessionAsync(new("test", "test", root.FullName, grants, null,
                new Dictionary<string, string?>(), new ShellPolicy(), true, false, 8192, TimeSpan.FromSeconds(30)), timeout.Token);
            await using var retained = await session.StartBackgroundAsync("printf retained", new BoundedOutputBuffer(), timeout.Token);
            await retained.Completion.WaitAsync(timeout.Token);
            Assert.Equal(0, retained.ExitCode);
            var retainedArtifacts = await session.Executor.RunAsync("find /tmp -maxdepth 1 -name 'oscova-bg-*' | sort");
            Assert.Equal(2, retainedArtifacts.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

            var buffer = new BoundedOutputBuffer();
            await using (var discarded = await session.StartBackgroundAsync(
                $"head -c {BoundedOutputBuffer.DefaultCapacityBytes} /dev/zero", buffer, timeout.Token))
            {
                await discarded.Completion.WaitAsync(timeout.Token);
                Assert.Equal(0, discarded.ExitCode);
                Assert.Equal(BoundedOutputBuffer.DefaultCapacityBytes, buffer.TotalSize);
            }

            var remainingArtifacts = await session.Executor.RunAsync("find /tmp -maxdepth 1 -name 'oscova-bg-*' | sort");
            Assert.Equal(0, remainingArtifacts.ExitCode);
            Assert.Equal(retainedArtifacts.Stdout, remainingArtifacts.Stdout);
        }
        finally { root.Delete(recursive: true); }
    }

    /// <summary>Reads flushed partial output and concurrent line output without inventing gaps or bytes.</summary>
    [DockerTheory]
    [Trait("Category", "Slow")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BackgroundOutput_PreservesFlushedBytesBeforeExit(bool lines, bool bounded)
    {
        var root = Directory.CreateTempSubdirectory("oscova-docker-output-");
        try
        {
            var template = PluginTestContext.Create(new MissingCredentials(), root.FullName, root.FullName);
            var context = new AgentPluginContext(template.CredentialResolver, template.Services,
                NullLoggerFactory.Instance, TimeProvider.System, root.FullName, root.FullName)
            { InstallationSettings = new EmptySettings(), PrivateDataDirectory = root.FullName };
            await using var provider = new DockerExecutionProvider(context);
            IReadOnlyList<AllowedDirectory> grants =
                [new(root.FullName, "output", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/output", true)];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var session = await provider.CreateSessionAsync(new("test", "test", root.FullName, grants, null,
                new Dictionary<string, string?>(), new ShellPolicy(), true, false, 8192, TimeSpan.FromSeconds(30)), timeout.Token);
            var buffer = new BoundedOutputBuffer();
            var command = lines
                ? "python3 -u -c 'import sys,time; [(sys.stdout.write(str(i) + chr(10)),sys.stdout.flush(),time.sleep(0.005)) for i in range(400)]'; while [ ! -f /workspace/output/release ]; do sleep 0.05; done; exit 3"
                : "printf stdout-ready; printf stderr-ready >&2; while [ ! -f /workspace/output/release ]; do sleep 0.05; done";
            var expected = lines ? string.Concat(Enumerable.Range(0, 400).Select(i => i + "\n")) : "stdout-readystderr-ready";
            if (bounded)
            {
                command = $"head -c {BoundedOutputBuffer.DefaultCapacityBytes * 2} /dev/zero | tr '\\0' x; while [ ! -f /workspace/output/release ]; do sleep 0.05; done";
                expected = new string('x', BoundedOutputBuffer.DefaultCapacityBytes * 2);
            }
            var readOffset = bounded ? expected.Length - BoundedOutputBuffer.DefaultCapacityBytes / 2 : 0;
            await using var job = await session.StartBackgroundAsync(command, buffer, timeout.Token);

            while (buffer.TotalSize < Encoding.UTF8.GetByteCount(expected))
            {
                await job.RefreshAsync(timeout.Token);
                var snapshot = buffer.ReadFrom(0, BoundedOutputBuffer.DefaultCapacityBytes);
                if (!bounded)
                {
                    Assert.False(snapshot.Truncated);
                    Assert.Equal(Encoding.UTF8.GetByteCount(snapshot.Text), snapshot.TotalSize);
                }
                await Task.Delay(20, timeout.Token);
            }

            Assert.False(job.HasExited);
            Assert.Equal(expected.Length, buffer.TotalSize);
            Assert.Equal(bounded, buffer.Truncated);
            Assert.Equal(expected[readOffset..], buffer.ReadFrom(readOffset, BoundedOutputBuffer.DefaultCapacityBytes).Text);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "release"), "", timeout.Token);
            while (!job.HasExited) { await job.RefreshAsync(timeout.Token); await Task.Delay(20, timeout.Token); }
            Assert.Equal(lines ? 3 : 0, job.ExitCode);
            Assert.Equal(expected[readOffset..], buffer.ReadFrom(readOffset, BoundedOutputBuffer.DefaultCapacityBytes).Text);
        }
        finally { root.Delete(recursive: true); }
    }

    [DockerTheory]
    [Trait("Category", "Slow")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ContainerMounts_ReadOnlyGrantAppliesToEveryAlias(bool nested, bool shellSession)
    {
        var root = Directory.CreateTempSubdirectory("oscova-docker-grants-");
        try
        {
            var safe = Directory.CreateDirectory(Path.Combine(root.FullName, "safe")).FullName;
            var protectedPath = Directory.CreateDirectory(Path.Combine(nested ? safe : root.FullName, "protected")).FullName;
            var template = PluginTestContext.Create(new MissingCredentials(), root.FullName, root.FullName);
            var context = new AgentPluginContext(template.CredentialResolver, template.Services,
                NullLoggerFactory.Instance, TimeProvider.System, root.FullName, root.FullName)
            { InstallationSettings = new EmptySettings(), PrivateDataDirectory = root.FullName };
            await using var provider = new DockerExecutionProvider(context);
            IReadOnlyList<AllowedDirectory> grants =
            [
                new(safe, "safe", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/safe", true),
                new(protectedPath, "mutable", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/mutable"),
                new(protectedPath, "protected", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly, "/workspace/protected")
            ];
            if (nested) { grants = [grants[0], grants[2]]; }
            var target = nested ? "/workspace/safe/protected/blocked.txt" : "/workspace/mutable/blocked.txt";
            var command = $"printf allowed > /workspace/safe/allowed.txt; if printf denied > {target}; then exit 9; fi";
            if (shellSession)
            {
                await using var session = await provider.CreateSessionAsync(new("test", "test", safe, grants, null,
                    new Dictionary<string, string?>(), new ShellPolicy(), true, false, 8192, TimeSpan.FromSeconds(30)), CancellationToken.None);
                var result = await session.Executor.RunAsync(command);
                Assert.True(result.ExitCode == 0, result.Stderr);
            }
            else
            {
                var launch = await provider.PrepareAsync(new ContainerProcessRequest("grant-check", "sh",
                    ["-c", command], new Dictionary<string, string?>(), grants, true), CancellationToken.None);
                var start = new ProcessStartInfo(launch.Command)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in launch.Arguments) { start.ArgumentList.Add(argument); }
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await output;
                Assert.True(process.ExitCode == 0, $"Exit {process.ExitCode}. Protected file exists: {File.Exists(Path.Combine(protectedPath, "blocked.txt"))}. {await error}");
            }
            Assert.False(File.Exists(Path.Combine(protectedPath, "blocked.txt")));
            Assert.Equal("allowed", await File.ReadAllTextAsync(Path.Combine(safe, "allowed.txt")));
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class EmptySettings : IPluginSettings
    {
        public Task<string?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string key, string content, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class MissingCredentials : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => ResolveAsync(key, ct);
    }

    private static string? DockerLiveSkipReason => Environment.GetEnvironmentVariable("OSCOVA_DOCKER_LIVE_TESTS") == "1"
        ? null
        : "Set OSCOVA_DOCKER_LIVE_TESTS=1 with Docker running and oscova-sandbox-runner:v3 already installed.";

    private sealed class DockerTheoryAttribute : TheoryAttribute
    {
        public DockerTheoryAttribute() => Skip = DockerLiveSkipReason;
    }

    private sealed class DockerFactAttribute : FactAttribute
    {
        public DockerFactAttribute() => Skip = DockerLiveSkipReason;
    }
}
