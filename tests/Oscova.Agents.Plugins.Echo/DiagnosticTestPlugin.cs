using Microsoft.Extensions.AI;
using Oscova.Agents.Llm;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Diagnostics;
using System.Reflection;

namespace Oscova.Agents.Plugins.Echo;

/// <summary>Bounded child fixtures for process failures and explicitly selected native lifetime checks.</summary>
public sealed class DiagnosticTestPlugin : IAgentPlugin
{
    public void Configure(AgentPluginBuilder builder) => builder.AddModelProvider(context => new DiagnosticTestProvider(context));
}

internal sealed partial class DiagnosticTestProvider(AgentPluginContext context) : IModelProvider, ILocalModelDiagnosticProvider
{
    public string Type => "diagnostic-failure-fixture";
    public string? DefaultEndpoint => null;
    public string? DefaultModel => null;
    public bool? DefaultIsLocalEndpoint => true;
    public bool SupportsEmbedding => false;

    public async Task<LocalModelDiagnosticResult> RunDiagnosticAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        if (config.ModelId == "native-lifetime") { return await CheckNativeLifetimeAsync(config, cancellationToken).ConfigureAwait(false); }
        if (config.ModelId == "native-benchmark") { return await RunNativeBenchmarkAsync(config, cancellationToken).ConfigureAwait(false); }

        if (config.ModelId is "native-coexistence" or "native-foundry-stage") { return await RunNativeCoexistenceAsync(config, cancellationToken).ConfigureAwait(false); }

        if (config.ModelId == "native-host-lifecycle")
        {
            var entry = Assembly.GetEntryAssembly()!.EntryPoint!.DeclaringType!;
            return await ((Task<LocalModelDiagnosticResult>)entry.GetMethod("RunNativeHostAsync", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [config, cancellationToken])!).ConfigureAwait(false);
        }

        if (config.ModelId == "unavailable-result")
        {
            return new("failed", "model-unavailable", "Select an available model and retry.");
        }

        var directory = context.ModelStorageDirectory;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "started"), Environment.ProcessId.ToString(), cancellationToken).ConfigureAwait(false);
        if (config.ModelId == "passive-child")
        {
            var child = new Process
            {
                StartInfo = new(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            child.StartInfo.ArgumentList.Add("--local-diagnostic-worker");
            // Register a deliberately waiting command with the same cleanup owner as passive discovery.
            var owner = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "Oscova.Agents.Infrastructure")
                .GetType("Oscova.Agents.Infrastructure.Hardware.LocalDiagnosticWorker", throwOnError: true)!;
            await ((Task)owner.GetMethod("StartPassiveCommandAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [child, cancellationToken])!).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "descendant"), child.Id.ToString(), cancellationToken).ConfigureAwait(false);
        }

        if (config.ModelId == "crash") { Environment.Exit(17); }

        if (config.ModelId == "stderr")
        {
            await Console.Error.WriteAsync(new string('x', 1024 * 1024)).ConfigureAwait(false);
            Environment.Exit(17);
        }

        if (config.ModelId == "oversize") { return new("failed", "fixture", new string('x', 70 * 1024), 0, 0, 0, null); }

        // Ignore cancellation to model an unresponsive native call, never a successful inference.
        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
        throw new InvalidOperationException("Unreachable.");
    }

    private IModelProvider CreateNativeProvider(ModelProviderConfig request)
    {
        // Nested loading is a test fixture, not proof of the host's plugin admission path.
        var plugin = AgentPluginLoader.LoadDirectory(request.Options!["pluginDirectory"]).Plugins.Single();
        var nativeContext = new AgentPluginContext(context.CredentialResolver, context.Services, context.LoggerFactory,
            context.TimeProvider, plugin.PluginDirectory, context.ModelStorageDirectory);
        return plugin.ModelProviders.Single().Factory(nativeContext);
    }

    private async Task<LocalModelDiagnosticResult> CheckNativeLifetimeAsync(ModelProviderConfig request, CancellationToken ct)
    {
        var provider = CreateNativeProvider(request);
        using var providerLifetime = (IDisposable)provider;
        var lifecycle = (IModelProviderConfiguration)provider;
        var config = new ModelProviderConfig
        {
            Id = Guid.NewGuid(), Type = ModelProviderKeys.Local, ModelId = request.Options!["modelId"], ContextWindowSize = 512
        };
        var clients = await Task.WhenAll(provider.CreateClientAsync(config, cancellationToken: ct).AsTask(),
            provider.CreateClientAsync(config, cancellationToken: ct).AsTask()).ConfigureAwait(false);
        using var first = clients[0];
        using var second = clients[1];
        var assembly = AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly)!.Assemblies.Single(value => value.GetName().Name == "LLamaSharp");
        var executorType = assembly.GetType("LLama.Abstractions.ILLamaExecutor", throwOnError: true)!;

        (SafeHandle Context, SafeHandle Weights) Handles(IChatClient client)
        {
            var executor = client.GetService(executorType)!;
            var native = executor.GetType().GetProperty("Context")!.GetValue(executor)!;
            var handle = (SafeHandle)native.GetType().GetProperty("NativeHandle")!.GetValue(native)!;
            return (handle, (SafeHandle)handle.GetType().GetProperty("ModelHandle")!.GetValue(handle)!);
        }

        static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }

        var firstHandles = Handles(first);
        var secondHandles = Handles(second);
        for (var call = 0; call < 3; call++)
        {
            var sampling = NativeTokenCounter.Create(assembly, firstHandles.Weights, 8);
            using var greedy = sampling.Pipeline;
            var requestOptions = new ChatOptions { MaxOutputTokens = 8, RawRepresentationFactory = _ => sampling.Parameters };
            if (call == 2)
            {
                await foreach (var update in first.GetStreamingResponseAsync("Reply with OK.", requestOptions, ct).ConfigureAwait(false)) { }
            }
            else { await first.GetResponseAsync("Reply with OK.", requestOptions, ct).ConfigureAwait(false); }

            Require(sampling.Counter.SampledTokens is > 0 and <= 8, $"Native request {call + 1} sampled {sampling.Counter.SampledTokens} tokens outside its output budget.");
        }

        Require(!ReferenceEquals(firstHandles.Context, secondHandles.Context), "Concurrent clients must own distinct native contexts.");
        Require(ReferenceEquals(firstHandles.Weights, secondHandles.Weights), "One configuration must share native weights.");
        second.Dispose();
        second.Dispose();
        Require(secondHandles.Context.IsClosed && !firstHandles.Weights.IsClosed, "Disposing one client must close only its context.");
        using var replacement = await provider.CreateClientAsync(config, cancellationToken: ct).ConfigureAwait(false);
        var replacementHandles = Handles(replacement);
        Require(ReferenceEquals(firstHandles.Weights, replacementHandles.Weights), "Client disposal must retain cached weights.");

        var otherConfig = config.Clone();
        otherConfig.Id = Guid.NewGuid();
        using var other = await provider.CreateClientAsync(otherConfig, cancellationToken: ct).ConfigureAwait(false);
        var otherHandles = Handles(other);
        Require(!ReferenceEquals(firstHandles.Weights, otherHandles.Weights), "Different configurations must own independent weights.");
        ChatMessage[] messages = [new(ChatRole.User, "Count from one to ten.")];
        var options = new ChatOptions { MaxOutputTokens = 32, Temperature = 0 };
        var stream = first.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
        Task<ChatResponse>? queued = null;
        try
        {
            Require(await stream.MoveNextAsync().ConfigureAwait(false), "The real native stream must yield before retirement.");
            queued = first.GetResponseAsync(messages, options, ct);
            Require(!queued.IsCompleted, "A second call must wait while the native stream holds the context.");
            await lifecycle.ReleaseConfigurationAsync(config.Id, ct).ConfigureAwait(false);
            Require(replacementHandles.Context.IsClosed, "Retirement must close idle contexts.");
            Require(!firstHandles.Context.IsClosed && !firstHandles.Weights.IsClosed, "Retirement must preserve the active native call.");
            Require(!otherHandles.Context.IsClosed && !otherHandles.Weights.IsClosed, "Retirement must preserve the other configuration.");
        }
        finally { await stream.DisposeAsync().ConfigureAwait(false); }

        var rejected = false;
        try { await queued!.ConfigureAwait(false); }
        catch (ObjectDisposedException) { rejected = true; }

        Require(rejected, "A queued call must reject a retired context.");
        Require(firstHandles.Context.IsClosed && firstHandles.Weights.IsClosed, "Draining the final call must free its context and weights.");
        first.Dispose();
        first.Dispose();
        using var fresh = await provider.CreateClientAsync(config, cancellationToken: ct).ConfigureAwait(false);
        var freshHandles = Handles(fresh);
        Require(!ReferenceEquals(firstHandles.Weights, freshHandles.Weights) && !freshHandles.Weights.IsClosed,
            "Recreating a released configuration must allocate live weights.");
        await lifecycle.ReleaseConfigurationAsync(config.Id, ct).ConfigureAwait(false);
        await lifecycle.ReleaseConfigurationAsync(otherConfig.Id, ct).ConfigureAwait(false);
        Require(freshHandles.Context.IsClosed && freshHandles.Weights.IsClosed && otherHandles.Context.IsClosed && otherHandles.Weights.IsClosed,
            "Final retirement must free every remaining native allocation.");
        return new("passed", "native-lifetime-verified", "Native context ownership, serialized calls and retirement verified.");
    }

    public ValueTask<IChatClient> CreateClientAsync(ModelProviderConfig config, string? apiKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(ModelProviderConfig config, string? apiKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(ModelProviderConfig config, string? apiKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(bool Success, string Message)> TestAsync(ModelProviderConfig config, string? apiKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
