using System.Text.Json;
using System.Runtime.Loader;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Llm;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Plugins.Echo;

internal sealed partial class DiagnosticTestProvider
{
    private async Task<LocalModelDiagnosticResult> RunNativeCoexistenceAsync(ModelProviderConfig request, CancellationToken ct)
    {
        var catalog = AgentPluginLoader.LoadDirectory(request.Options!["pluginDirectory"]);
        AgentPluginContext PluginContext(LoadedAgentPlugin plugin) => new(context.CredentialResolver, context.Services, context.LoggerFactory,
            context.TimeProvider, plugin.PluginDirectory, context.ModelStorageDirectory);
        IModelProvider ModelProvider(string id)
        {
            var plugin = catalog.Plugins.Single(plugin => plugin.Manifest.Id == id);
            return plugin.ModelProviders.Single().Factory(PluginContext(plugin));
        }

        const string foundryAlias = "qwen3-0.6b";
        if (request.ModelId == "native-foundry-stage")
        {
            var staging = ModelProvider("oscova.foundry-local");
            using var stagingLifetime = (IDisposable)staging;
            var model = (await staging.GetAvailableModelsAsync(new(), cancellationToken: ct).ConfigureAwait(false)).Single(model => model.Id == foundryAlias);
            if (model.IsInstalled != true)
            {
                await ((IModelDownloadProvider)staging).DownloadModelAsync(new() { Type = staging.Type, ModelId = model.Id }, cancellationToken: ct).ConfigureAwait(false);
            }

            return new("passed", "native-foundry-staged", foundryAlias);
        }

        IModelProvider? llama = null;
        IModelProvider? foundry = null;
        IChatClient? llamaClient = null;
        IChatClient? foundryClient = null;
        ISpeechSynthesisProvider? synthesis = null;
        ISpeechRecognitionProvider? recognition = null;
        var speechConfig = new SpeechProviderConfig { Type = SpeechProviderKeys.Local };
        var session = Guid.NewGuid().ToString("N");
        var observations = new List<object>();
        var nativeLog = new NativeBenchmarkLog();
        async Task RunLlamaAsync(bool streaming = false)
        {
            if (llama is null)
            {
                llama = ModelProvider("oscova.llamasharp");
                var nativeAssembly = AssemblyLoadContext.GetLoadContext(llama.GetType().Assembly)!.Assemblies.Single(value => value.GetName().Name == "LLamaSharp");
                nativeAssembly.GetType("LLama.Native.NativeLogConfig", throwOnError: true)!.GetMethod("llama_log_set", [typeof(ILogger)])!.Invoke(null, [nativeLog]);
            }

            llamaClient ??= await llama.CreateClientAsync(new()
            {
                Id = Guid.NewGuid(), Type = llama.Type, ModelId = request.Options["modelId"], ContextWindowSize = 512
            }, cancellationToken: ct).ConfigureAwait(false);
            var assembly = AssemblyLoadContext.GetLoadContext(llama.GetType().Assembly)!.Assemblies.Single(value => value.GetName().Name == "LLamaSharp");
            var executor = llamaClient.GetService(assembly.GetType("LLama.Abstractions.ILLamaExecutor", throwOnError: true)!)!;
            var nativeContext = executor.GetType().GetProperty("Context")!.GetValue(executor)!;
            var handle = nativeContext.GetType().GetProperty("NativeHandle")!.GetValue(nativeContext)!;
            var weights = handle.GetType().GetProperty("ModelHandle")!.GetValue(handle)!;
            var sampling = NativeTokenCounter.Create(assembly, weights, 8);
            using var greedy = sampling.Pipeline;
            var counter = sampling.Counter;
            var options = new ChatOptions { MaxOutputTokens = 8, RawRepresentationFactory = _ => sampling.Parameters };
            var text = new StringBuilder();
            if (streaming)
            {
                await foreach (var update in llamaClient.GetStreamingResponseAsync("Reply with OK.", options, ct).ConfigureAwait(false)) { text.Append(update.Text); }
            }
            else { text.Append((await llamaClient.GetResponseAsync("Reply with OK.", options, ct).ConfigureAwait(false)).Text); }

            if (counter.SampledTokens is <= 0 or > 8)
            {
                throw new InvalidOperationException("LLamaSharp did not sample within the output budget: " + JsonSerializer.Serialize(new { PriorObservations = observations, counter.SampledTokens, counter.OutputTokens, counter.EndOfGeneration, Text = text.ToString() }));
            }

            observations.Add(new { Provider = "LLamaSharp", Streaming = streaming, counter.SampledTokens, counter.OutputTokens, counter.EndOfGeneration, Text = text.ToString(), NativeObservations = nativeLog.Read() });
        }

        async Task RunFoundryAsync()
        {
            foundry ??= ModelProvider("oscova.foundry-local");
            foundryClient ??= await foundry.CreateClientAsync(new() { ModelId = foundryAlias }, cancellationToken: ct).ConfigureAwait(false);
            var reply = await foundryClient.GetResponseAsync("Reply with OK.", new() { MaxOutputTokens = 8 }, ct).ConfigureAwait(false);
            if (reply.Usage?.OutputTokenCount is not > 0)
            {
                throw new InvalidOperationException("Foundry did not report generated tokens: " + JsonSerializer.Serialize(new { reply.ModelId, reply.Usage, reply.FinishReason }));
            }

            observations.Add(new { Provider = "Foundry", SelectedModelId = foundryClient.GetService<ChatClientMetadata>()?.DefaultModelId, reply.ModelId, reply.Usage, reply.FinishReason, reply.Text });
        }

        async Task RunSpeechAsync()
        {
            if (synthesis is null)
            {
                var plugin = catalog.Plugins.Single(plugin => plugin.Manifest.Id == "oscova.local-speech-synthesis");
                synthesis = plugin.SpeechSynthesisProviders.Single().Factory(PluginContext(plugin));
                await synthesis.StartSessionAsync(session, speechConfig, null, "af_heart", ct).ConfigureAwait(false);
            }

            var response = await synthesis.SynthesizeAsync(session, speechConfig, null,
                new SpeechSynthesisRequest { Text = "Hello from Oscova.", IncludeWavHeader = true }, ct).ConfigureAwait(false);
            var audio = response.Audio;
            if (audio is not { Length: > 44 }) { throw new InvalidOperationException("Synthesis returned no WAV audio."); }

            if (recognition is null)
            {
                var plugin = catalog.Plugins.Single(plugin => plugin.Manifest.Id == "oscova.local-speech-recognition");
                recognition = plugin.SpeechRecognitionProviders.Single().Factory(PluginContext(plugin));
            }

            var transcript = await recognition.TranscribeAsync(speechConfig, null, audio, "en", ct).ConfigureAwait(false);
            if (!transcript.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException($"Recognition did not hear the synthesized greeting: {transcript.Text}"); }

            observations.Add(new { Provider = "Speech", AudioBytes = audio.Length, transcript.Text });
        }

        try
        {
            if (request.Options["order"] == "llama-first")
            {
                await RunLlamaAsync().ConfigureAwait(false);
                await RunSpeechAsync().ConfigureAwait(false);
                await RunFoundryAsync().ConfigureAwait(false);
            }
            else
            {
                await RunFoundryAsync().ConfigureAwait(false);
                await RunSpeechAsync().ConfigureAwait(false);
                await RunLlamaAsync().ConfigureAwait(false);
            }

            // Exercise every retained stack after the other native runtimes have loaded.
            await RunLlamaAsync().ConfigureAwait(false);
            await RunSpeechAsync().ConfigureAwait(false);
            await RunFoundryAsync().ConfigureAwait(false);
            await RunLlamaAsync(streaming: true).ConfigureAwait(false);
            return new("passed", "native-coexistence-completed", JsonSerializer.Serialize(observations));
        }
        finally
        {
            if (synthesis is not null) { await synthesis.StopSessionAsync(session, speechConfig, CancellationToken.None).ConfigureAwait(false); }

            llamaClient?.Dispose();
            foundryClient?.Dispose();
            (llama as IDisposable)?.Dispose();
            (foundry as IDisposable)?.Dispose();
            (synthesis as IDisposable)?.Dispose();
            (recognition as IDisposable)?.Dispose();
        }
    }
}
