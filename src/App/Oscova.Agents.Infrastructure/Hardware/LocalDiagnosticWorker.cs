using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Core.Hardware;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Llm;
using Oscova.Agents.Plugins;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Infrastructure.Hardware;

/// <summary>Runs passive discovery or one installed local model outside the serving process.</summary>
public static class LocalDiagnosticWorker
{
    public const string Argument = "--local-diagnostic-worker";
    internal const int MessageLimit = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim PassiveCommandGate = new(1, 1);
    private static Process? _passiveCommand;
    private static bool _terminating;

    /// <summary>Dispatches before CLI or web startup, returning false for ordinary application invocations.</summary>
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != Argument) { return false; }

        using var watchdog = new CancellationTokenSource();
        var deadlineMonitor = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(65), watchdog.Token).ConfigureAwait(false); await TerminateAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        });
        var output = Console.Out;
        Process? parent = null;
        Task parentMonitor = Task.CompletedTask;
        Console.SetOut(TextWriter.Null);
        try
        {
            var requestText = await ReadBoundedAsync(Console.In, MessageLimit, watchdog.Token).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<DiagnosticRequest>(requestText, Json) ?? throw new InvalidDataException("Missing request.");
            parent = Process.GetProcessById(request.ParentId);
            if (ProcessIdentity.GetStartIdentifier(parent) != request.ParentStartIdentifier) { throw new InvalidDataException("Parent identity changed."); }

            parentMonitor = Task.Run(async () =>
            {
                try
                {
                    while (!watchdog.IsCancellationRequested)
                    {
                        if (parent.HasExited || DateTimeOffset.UtcNow >= request.Deadline)
                        {
                            await TerminateAsync().ConfigureAwait(false);
                            return;
                        }

                        await Task.Delay(250, watchdog.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch { await TerminateAsync().ConfigureAwait(false); }
            });

            DiagnosticResponse result;
            if (request.Mode == "passive")
            {
                result = new(await HostHardwareProbe.CollectAsync(request.ProcessMemoryBudgetBytes, watchdog.Token).ConfigureAwait(false), null);
            }
            else if (request.Mode == "model" && request.Config is not null && request.PluginDirectory is not null)
            {
                var inspections = AgentPluginLoader.InspectDirectory(request.PluginDirectory);
                if (inspections.Count != 1 || inspections[0].Diagnostic is not null ||
                    inspections[0].Manifest is not { } manifest || manifest.Id != request.PluginId || manifest.Version != request.PluginVersion ||
                    !manifest.SupportsRuntimeIdentifier(AgentPluginLoader.SupportedRuntimeIdentifier))
                { throw new InvalidDataException("The admitted plugin identity or platform changed."); }

                var catalog = AgentPluginLoader.LoadDirectory(request.PluginDirectory);
                var plugin = catalog.Plugins.Single();
                var unavailable = new UnavailableServices();
                var context = new AgentPluginContext(unavailable, unavailable, NullLoggerFactory.Instance,
                    TimeProvider.System, plugin.PluginDirectory, request.ModelDirectory!);
                IModelProvider? selected = null;
                try
                {
                    if (request.ContributionIndex < 0 || request.ContributionIndex >= plugin.ModelProviders.Count)
                    { throw new InvalidDataException("The admitted provider contribution changed."); }

                    selected = plugin.ModelProviders[request.ContributionIndex].Factory(context);
                    if (selected.Type != request.Config.Type) { throw new InvalidDataException("The admitted provider identity changed."); }

                    if (selected is not ILocalModelDiagnosticProvider diagnostic) { throw new NotSupportedException("The selected provider does not support isolated diagnostics."); }

                    var errors = (selected as IModelProviderConfiguration)?.ValidateConfiguration(request.Config);
                    if (errors is { Count: > 0 }) { throw new InvalidDataException(string.Join(" ", errors.Values)); }

                    result = new(null, await diagnostic.RunDiagnosticAsync(request.Config, watchdog.Token).ConfigureAwait(false));
                }
                finally
                {
                    if (selected is IAsyncDisposable asyncDisposable) { await asyncDisposable.DisposeAsync().ConfigureAwait(false); }
                    else if (selected is IDisposable disposable) { disposable.Dispose(); }
                }
            }
            else { throw new InvalidDataException("Unknown diagnostic mode."); }

            var serialized = JsonSerializer.Serialize(result, Json);
            if (Encoding.UTF8.GetByteCount(serialized) > MessageLimit) { throw new InvalidDataException("Diagnostic result exceeds 64 KiB."); }

            await output.WriteAsync(serialized).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var detail = ex.ToString();
            await Console.Error.WriteAsync(detail.AsMemory(0, Math.Min(detail.Length, 32 * 1024)), CancellationToken.None).ConfigureAwait(false);
            Environment.ExitCode = 1;
        }
        finally
        {
            await watchdog.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(deadlineMonitor, parentMonitor).ConfigureAwait(false);
            parent?.Dispose();
        }

        return true;
    }

    private static async Task TerminateAsync()
    {
        await PassiveCommandGate.WaitAsync().ConfigureAwait(false);
        try { _terminating = true; }
        finally { PassiveCommandGate.Release(); }

        if (await StopPassiveCommandAsync().ConfigureAwait(false)) { Environment.Exit(3); }
    }

    internal static async Task StartPassiveCommandAsync(Process process, CancellationToken ct)
    {
        await PassiveCommandGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_terminating) { throw new OperationCanceledException("The diagnostic worker is terminating.", ct); }

            if (!process.Start()) { throw new IOException("The hardware command did not start."); }

            _passiveCommand = process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
        finally { PassiveCommandGate.Release(); }
    }

    internal static async Task<bool> StopPassiveCommandAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await PassiveCommandGate.WaitAsync(cleanup.Token).ConfigureAwait(false);
            try
            {
                if (_passiveCommand is null) { return true; }

                if (!_passiveCommand.HasExited) { _passiveCommand.Kill(entireProcessTree: true); }

                await _passiveCommand.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                _passiveCommand.Dispose();
                _passiveCommand = null;
                return true;
            }
            finally { PassiveCommandGate.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"Hardware command cleanup is unconfirmed: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    internal static Process CreateProcess()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The current executable path is unavailable.");
        var entry = Assembly.GetEntryAssembly()?.Location;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(entry)) { throw new InvalidOperationException("The entry assembly is unavailable."); }

            start.ArgumentList.Add(entry);
        }

        start.ArgumentList.Add(Argument);
        return new Process { StartInfo = start };
    }

    internal static async Task<string> ReadBoundedAsync(TextReader reader, int maximumBytes, CancellationToken ct, bool truncate = false)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        var bytes = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (bytes > maximumBytes)
            {
                if (!truncate) { throw new InvalidDataException("Diagnostic output exceeds its byte limit."); }

                bytes = maximumBytes;
                continue;
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    internal static string Serialize(DiagnosticRequest request) => JsonSerializer.Serialize(request, Json);
    internal static DiagnosticResponse Deserialize(string text) => JsonSerializer.Deserialize<DiagnosticResponse>(text, Json)
        ?? throw new InvalidDataException("The diagnostic result is empty.");

    private sealed class UnavailableServices : IAgentPluginServices, IToolCredentialResolver
    {
        public IPluginEncryptionService Encryption => throw new NotSupportedException("Diagnostic encryption is unavailable.");
        public IToolOutputStore? ToolOutputStore => throw new NotSupportedException("Diagnostic storage is unavailable.");
        public HttpClient CreateHttpClient() => throw new NotSupportedException("Diagnostic networking is unavailable.");
        public HttpMessageHandler CreateHttpMessageHandler() => throw new NotSupportedException("Diagnostic networking is unavailable.");
        public Task EnsureDownloadAsync(PluginDownloadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("Diagnostic downloads are unavailable.");
        public Task<string?> ResolveAccessTokenAsync(string providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException("Diagnostic credentials are unavailable.");
        public Task<string?> ResolveAsync(string credentialKey, CancellationToken ct = default) => throw new NotSupportedException("Diagnostic credentials are unavailable.");
        public Task<string?> ResolveAsync(string credentialKey, string fieldName, CancellationToken ct = default) => throw new NotSupportedException("Diagnostic credentials are unavailable.");
    }
}

/// <summary>Carries only host-admitted paths and a non-secret snapshot over the private worker pipe.</summary>
internal sealed record DiagnosticRequest(string Mode, int ParentId, long ParentStartIdentifier, DateTimeOffset Deadline,
    long? ProcessMemoryBudgetBytes = null, string? PluginId = null, string? PluginVersion = null,
    string? PluginDirectory = null, string? ModelDirectory = null, ModelProviderConfig? Config = null, int ContributionIndex = -1);

/// <summary>Returns passive observations or one inference result through a bounded worker pipe.</summary>
internal sealed record DiagnosticResponse(HostHardwareSnapshot? Hardware, LocalModelDiagnosticResult? Model);
