using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Plugins.Echo;

internal sealed partial class DiagnosticTestProvider
{
    private async Task<LocalModelDiagnosticResult> RunNativeBenchmarkAsync(ModelProviderConfig request, CancellationToken ct)
    {
        var provider = CreateNativeProvider(request);
        using var providerLifetime = (IDisposable)provider;
        var assembly = AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly)!.Assemblies.Single(value => value.GetName().Name == "LLamaSharp");
        var executorType = assembly.GetType("LLama.Abstractions.ILLamaExecutor", throwOnError: true)!;
        var log = new NativeBenchmarkLog();
        assembly.GetType("LLama.Native.NativeLogConfig", throwOnError: true)!.GetMethod("llama_log_set", [typeof(ILogger)])!.Invoke(null, [log]);
        var config = new ModelProviderConfig
        {
            Id = Guid.NewGuid(), Type = ModelProviderKeys.Local, ModelId = request.Options!["modelId"], ContextWindowSize = 512
        };
        const string prompt = "Count from one to ten:\n";
        var runs = new List<object>();
        var coldLoadMilliseconds = 0d;
        for (var run = 0; run < 4; run++)
        {
            var creation = Stopwatch.StartNew();
            using var client = await provider.CreateClientAsync(config, cancellationToken: ct).ConfigureAwait(false);
            var creationMilliseconds = creation.Elapsed.TotalMilliseconds;
            if (run == 0) { coldLoadMilliseconds = creationMilliseconds; }

            var executor = client.GetService(executorType)!;
            var nativeContext = executor.GetType().GetProperty("Context")!.GetValue(executor)!;
            var handle = (SafeHandle)nativeContext.GetType().GetProperty("NativeHandle")!.GetValue(nativeContext)!;
            var weights = handle.GetType().GetProperty("ModelHandle")!.GetValue(handle)!;
            var sampling = NativeTokenCounter.Create(assembly, weights, 32);
            using var greedy = sampling.Pipeline;
            var counter = sampling.Counter;
            try
            {
                // Both versions use the same executor seam; 0.27's chat adapter replaces custom samplers.
                var generation = Stopwatch.StartNew();
                var stream = (IAsyncEnumerable<string>)executorType.GetMethod("InferAsync")!.Invoke(executor, [prompt, sampling.Parameters, ct])!;
                await foreach (var chunk in stream.WithCancellation(ct).ConfigureAwait(false)) { }

                runs.Add(new
                {
                    Warmup = run == 0, ContextCreationMilliseconds = creationMilliseconds,
                    GenerationMilliseconds = generation.Elapsed.TotalMilliseconds,
                    counter.SampledTokens, counter.OutputTokens, counter.EndOfGeneration
                });
            }
            finally
            {
                client.Dispose();
                // The historical provider retains contexts until shutdown; equalize live allocations outside timing.
                if (!handle.IsClosed) { ((IDisposable)nativeContext).Dispose(); }
            }
        }

        var observations = log.Read().Split('\n').Where(line => line.Contains("buffer size", StringComparison.Ordinal) || line.Contains("offloaded", StringComparison.Ordinal));
        var report = JsonSerializer.Serialize(new
        {
            RuntimeModuleVersionId = assembly.ManifestModule.ModuleVersionId,
            ContextSize = 512, Prompt = prompt, OutputLimit = 32,
            ColdLoadMilliseconds = coldLoadMilliseconds, Runs = runs, NativeObservations = string.Join('\n', observations)
        });
        return new("passed", "native-benchmark-completed", report);
    }

    private sealed class NativeBenchmarkLog : ILogger
    {
        private readonly StringBuilder _text = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_text)
            {
                var message = formatter(state, exception);
                if (!message.Contains("buffer size", StringComparison.Ordinal) && !message.Contains("offloaded", StringComparison.Ordinal)) { return; }

                var remaining = 32 * 1024 - _text.Length - 1;
                if (remaining > 0) { _text.Append(message.AsSpan(0, Math.Min(message.Length, remaining))).Append('\n'); }
            }
        }

        public string Read() { lock (_text) { return _text.ToString(); } }
    }
}

/// <summary>Counts sampled native tokens and EOS independently of emitted text chunks.</summary>
public class NativeTokenCounter : DispatchProxy
{
    internal static (object Parameters, NativeTokenCounter Counter, IDisposable Pipeline) Create(Assembly assembly, object weights, int outputLimit)
    {
        var parametersType = assembly.GetType("LLama.Common.InferenceParams", throwOnError: true)!;
        var pipelineType = assembly.GetType("LLama.Sampling.ISamplingPipeline", throwOnError: true)!;
        var greedy = (IDisposable)Activator.CreateInstance(assembly.GetType("LLama.Sampling.GreedySamplingPipeline", throwOnError: true)!)!;
        var counter = (NativeTokenCounter)Create(pipelineType, typeof(NativeTokenCounter));
        counter.Pipeline = greedy;
        counter.Weights = weights;
        var parameters = Activator.CreateInstance(parametersType)!;
        parametersType.GetProperty("MaxTokens")!.SetValue(parameters, outputLimit);
        parametersType.GetProperty("SamplingPipeline")!.SetValue(parameters, counter);
        return (parameters, counter, greedy);
    }

    public object Pipeline { get; set; } = null!;
    public object Weights { get; set; } = null!;
    public int SampledTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public bool EndOfGeneration { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var result = targetMethod!.Invoke(Pipeline, args);
        if (targetMethod.Name == "Sample")
        {
            SampledTokens++;
            var eos = (bool)result!.GetType().GetMethod("IsEndOfGeneration", [Weights.GetType()])!.Invoke(result, [Weights])!;
            EndOfGeneration |= eos;
            if (!eos) { OutputTokens++; }
        }

        return result;
    }
}
