using System.Diagnostics;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Execution;
using Oscova.Agents.Plugins.Docker;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Plugins.Tests.Docker;

/// <summary>Checks effective directory grants in a real disposable Docker process.</summary>
public sealed class DockerWorkspaceLiveTests
{
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

    private sealed class DockerTheoryAttribute : TheoryAttribute
    {
        public DockerTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("OSCOVA_DOCKER_LIVE_TESTS") != "1")
            { Skip = "Set OSCOVA_DOCKER_LIVE_TESTS=1 with Docker running and oscova-sandbox-runner:v3 already installed."; }
        }
    }
}
