using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;



namespace Oscova.Agents.Plugins.Docker;

/// <summary>
/// Ensures the local Docker sandbox image exists before container-backed shell execution starts.
/// </summary>
public sealed class SandboxImageBuilder : IAsyncDisposable
{
    public const string ImageTag = "oscova-sandbox-runner:v3";

    private const string BakedBaseImageTag = "node:22-bookworm-slim";
    private const string DockerUnavailableMessage = "Docker is not available. Install Docker Desktop / Docker Engine and ensure 'docker' is on PATH.";

    private readonly string BakedContextPath;
    private readonly string _customContextPath;

    private readonly DockerImageSettings _settings;
    private readonly ILogger<SandboxImageBuilder> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly Lock _preparationLock = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private CancellationTokenSource _preparation = new();

    /// <summary>Uses the configured clock for Docker readiness deadlines.</summary>
    public SandboxImageBuilder(DockerImageSettings settings, ILogger<SandboxImageBuilder> logger, TimeProvider timeProvider, string privateRoot)
    {
        BakedContextPath = Path.Combine(privateRoot, "image-build", "default");
        _customContextPath = Path.Combine(privateRoot, "image-build", "custom");
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>Requires Docker and its daemon to respond within ten seconds.</summary>
    public async Task EnsureDockerReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), _timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            // Keep failure details that a boolean availability probe would discard.
            ProcessResult result = await RunDockerAsync(["info"], timeout.Token, _logger).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                _logger.LogError("Docker readiness failed with exit code {ExitCode}: {ErrorText}", result.ExitCode, result.StandardError.Trim());
                throw new DockerReadinessException(DockerReadinessFailure.Unreachable);
            }
        }
        catch (Win32Exception ex)
        {
            DockerReadinessFailure reason = ex.NativeErrorCode == 2 || (OperatingSystem.IsWindows() && ex.NativeErrorCode == 3)
                ? DockerReadinessFailure.NotFound
                : DockerReadinessFailure.StartFailed;
            _logger.LogError(ex, "Docker readiness failed: {Reason}", reason);
            throw new DockerReadinessException(reason, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Docker readiness timed out after {TimeoutSeconds} seconds.", 10);
            throw new DockerReadinessException(DockerReadinessFailure.TimedOut, ex);
        }
        catch (InvalidOperationException ex) when (ex is not DockerReadinessException)
        {
            _logger.LogError(ex, "Docker readiness could not start the Docker process.");
            throw new DockerReadinessException(DockerReadinessFailure.StartFailed, ex);
        }
    }

    /// <summary>Ensures the sandbox image exists; caller cancellation stops only that caller's wait.</summary>
    public Task EnsureImageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_preparationLock) { return EnsureSerializedAsync(false, _preparation.Token).WaitAsync(cancellationToken); }
    }

    /// <summary>Ensures the image exists without network access; caller cancellation stops only that caller's wait.</summary>
    internal Task EnsureOfflineImageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_preparationLock) { return EnsureSerializedAsync(true, _preparation.Token).WaitAsync(cancellationToken); }
    }

    private async Task EnsureSerializedAsync(bool networkDisabled, CancellationToken ct)
    {
        await _buildGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _settings.LoadAsync(ct).ConfigureAwait(false);
            DockerImageSettings.Validate(_settings.CustomDockerfile);
            await EnsureCoreAsync(networkDisabled, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _buildGate.Release();
        }
    }

    private async Task EnsureCoreAsync(bool networkDisabled, CancellationToken ct)
    {
        if (await ImageBuiltAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        if (networkDisabled && _settings.HasCustomDockerfile)
        {
            throw new InvalidOperationException("Offline mode is on: build the custom sandbox image before starting the run.");
        }

        if (networkDisabled && !await ImageBuiltAsync(BakedBaseImageTag, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Offline mode is on: the local base image '{BakedBaseImageTag}' is required to build the sandbox image.");
        }

        await BuildImageAsync(networkDisabled, ct).ConfigureAwait(false);
    }

    /// <summary>Cancels and drains automatic preparation before admitting a later configuration.</summary>
    public async Task StopAsync()
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource previous;
            lock (_preparationLock) { previous = _preparation; }
            await previous.CancelAsync().ConfigureAwait(false);
            await _buildGate.WaitAsync().ConfigureAwait(false);
            try { lock (_preparationLock) { _preparation = new(); } }
            finally { _buildGate.Release(); previous.Dispose(); }
        }
        finally { _stopGate.Release(); }
    }

    /// <summary>Stops automatic image preparation before releasing its device runtime.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _preparation.Dispose();
        _buildGate.Dispose();
        _stopGate.Dispose();
    }

    /// <summary>
    /// Returns whether Docker can find the sandbox image tag locally.
    /// </summary>
    public static async Task<bool> ImageBuiltAsync(CancellationToken cancellationToken)
    {
        return await ImageBuiltAsync(ImageTag, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ImageBuiltAsync(string imageTag, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await RunDockerAsync(
                ["image", "inspect", imageTag],
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(DockerUnavailableMessage, ex);
        }
    }

    private async Task BuildImageAsync(bool networkDisabled, CancellationToken cancellationToken)
    {
        DockerImageSettings.Validate(_settings.CustomDockerfile);
        string contextPath = ResolveContextPath();
        if (!Directory.Exists(contextPath))
        {
            throw new InvalidOperationException($"Docker build context directory was not found: {contextPath}");
        }

        try
        {
            IReadOnlyList<string> arguments = networkDisabled
                ? ["build", "--quiet", "--network", "none", "--pull=false", "-t", ImageTag, contextPath]
                : ["build", "--quiet", "-t", ImageTag, contextPath];
            ProcessResult result = await RunDockerAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                _logger.LogError(
                    "Docker sandbox image build failed with exit code {ExitCode}.", result.ExitCode);
                throw new InvalidOperationException($"Docker sandbox image build failed with exit code {result.ExitCode}. Rebuild from Execution settings to inspect its output.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(DockerUnavailableMessage, ex);
        }
    }

    /// <summary>Force-rebuilds the sandbox image tag with caller cancellation and streamed output.</summary>
    public async Task<bool> RebuildAsync(
        bool noCache,
        Action<string, bool> onOutput,
        CancellationToken cancellationToken)
    {
        await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DockerImageSettings.Validate(_settings.CustomDockerfile);
            string contextPath = ResolveContextPath();
            if (!Directory.Exists(contextPath))
            {
                throw new InvalidOperationException($"Docker build context directory was not found: {contextPath}");
            }

            var arguments = new List<string> { "build", "-t", ImageTag };
            if (noCache)
            {
                arguments.Add("--no-cache");
            }

            arguments.Add(contextPath);

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
                ProcessResult result = await RunDockerStreamingAsync(arguments, onOutput, timeoutCts.Token, _logger).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    _logger.LogError(
                        "Docker sandbox image rebuild failed with exit code {ExitCode}.",
                        result.ExitCode);
                }

                return result.ExitCode == 0;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Docker sandbox image build timed out.", ex);
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(DockerUnavailableMessage, ex);
            }
        }
        finally
        {
            _ = _buildGate.Release();
        }
    }

    internal string ResolveContextPath()
    {
        MaterializeDefaultContext();
        if (!_settings.HasCustomDockerfile)
        {
            return BakedContextPath;
        }

        Directory.CreateDirectory(_customContextPath);
        File.WriteAllText(Path.Combine(_customContextPath, "Dockerfile"), _settings.CustomDockerfile);
        return _customContextPath;
    }

    /// <summary>Reads the embedded default Dockerfile without probing Docker.</summary>
    public static string ReadDefaultDockerfile()
    {
        using var stream = typeof(SandboxImageBuilder).Assembly.GetManifestResourceStream("Oscova.Agents.Plugins.Docker.BuildContext.Dockerfile")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private void MaterializeDefaultContext()
    {
        Directory.CreateDirectory(BakedContextPath);
        var assembly = typeof(SandboxImageBuilder).Assembly;
        const string Prefix = "Oscova.Agents.Plugins.Docker.BuildContext.";
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var input = assembly.GetManifestResourceStream(name)!;
            using var output = File.Create(Path.Combine(BakedContextPath, name[Prefix.Length..]));
            input.CopyTo(output);
        }
    }

    private static async Task<ProcessResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) { throw new InvalidOperationException("Docker process could not be started."); }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process, stdoutTask, stderrTask, logger).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<ProcessResult> RunDockerStreamingAsync(
        IReadOnlyList<string> arguments,
        Action<string, bool> onOutput,
        CancellationToken cancellationToken, ILogger logger)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) { throw new InvalidOperationException("Docker process could not be started."); }
        Task stdoutTask = StreamLinesAsync(process.StandardOutput, isError: false, onOutput, cancellationToken);
        Task stderrTask = StreamLinesAsync(process.StandardError, isError: true, onOutput, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, string.Empty, string.Empty);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process, stdoutTask, stderrTask, logger).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Drains both automatic and streamed Docker processes before cancellation completes.</summary>
    private static async Task StopProcessAsync(Process process, Task stdout, Task stderr, ILogger? logger)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        catch (InvalidOperationException) { }
        catch (Win32Exception ex)
        {
            logger?.LogError(ex, "Failed to terminate cancelled Docker process {ProcessId}.", process.Id);
            throw;
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static async Task StreamLinesAsync(
        StreamReader reader,
        bool isError,
        Action<string, bool> onOutput,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            onOutput(line, isError);
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
