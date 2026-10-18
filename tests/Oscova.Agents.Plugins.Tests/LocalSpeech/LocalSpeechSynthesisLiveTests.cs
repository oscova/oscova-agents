using Oscova.Agents.Plugins.LocalSpeechSynthesis;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Plugins.Tests.LocalSpeech;

/// <summary>Exercises session shutdown with the actual Kokoro model and native inference runtime.</summary>
public sealed class LocalSpeechSynthesisLiveTests
{
    [KokoroFact]
    [Trait("Category", "Slow")]
    public async Task StartSession_RejectsUnknownVoiceOnEveryAttempt()
    {
        var context = PluginTestContext.Create(new MissingCredentials(),
            Environment.GetEnvironmentVariable("OSCOVA_KOKORO_PLUGIN_DIRECTORY")!,
            Environment.GetEnvironmentVariable("OSCOVA_KOKORO_MODEL_DIRECTORY")!);
        var builder = new AgentPluginBuilder();
        new LocalSpeechSynthesisAgentPlugin().Configure(builder);
        var provider = Assert.Single(builder.SpeechSynthesisProviders).Factory(context);
        using var lifetime = (IDisposable)provider;
        var config = new SpeechProviderConfig();
        await provider.StartSessionAsync("test", config, null, "af_heart");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartSessionAsync("test", config, null, "missing-voice"));
        }
        await provider.StartSessionAsync("test", config, null, "af_heart");
    }

    [KokoroFact]
    [Trait("Category", "Slow")]
    public async Task QueuedSynthesis_PreservesEachRequestsVoice()
    {
        var context = PluginTestContext.Create(new MissingCredentials(),
            Environment.GetEnvironmentVariable("OSCOVA_KOKORO_PLUGIN_DIRECTORY")!,
            Environment.GetEnvironmentVariable("OSCOVA_KOKORO_MODEL_DIRECTORY")!);
        var builder = new AgentPluginBuilder();
        new LocalSpeechSynthesisAgentPlugin().Configure(builder);
        var provider = Assert.Single(builder.SpeechSynthesisProviders).Factory(context);
        using var lifetime = (IDisposable)provider;
        var config = new SpeechProviderConfig();
        await provider.StartSessionAsync("test", config, null, "af_heart");
        var request = new SpeechSynthesisRequest { Text = "Hello, this is my voice.", VoiceId = "af_heart" };
        var reference = await provider.SynthesizeAsync("test", config, null, request);
        var alternate = new SpeechSynthesisRequest { Text = request.Text, VoiceId = "af_bella" };
        var alternateReference = await provider.SynthesizeAsync("test", config, null, alternate);
        var first = provider.SynthesizeAsync("test", config, null, new SpeechSynthesisRequest
        {
            Text = string.Join(' ', Enumerable.Repeat("This is a local synthesis lifecycle check.", 10))
        });
        Assert.False(first.IsCompleted);
        var second = provider.SynthesizeAsync("test", config, null, request);
        var third = provider.SynthesizeAsync("test", config, null, alternate);
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(reference.Audio, second.Result.Audio);
        Assert.Equal(alternateReference.Audio, third.Result.Audio);
        Assert.NotEqual(second.Result.Audio.Length, third.Result.Audio.Length);
    }

    [KokoroFact]
    [Trait("Category", "Slow")]
    public async Task StopSession_DrainsActiveSynthesisWithoutDisposingItsGate()
    {
        var context = PluginTestContext.Create(new MissingCredentials(),
            Environment.GetEnvironmentVariable("OSCOVA_KOKORO_PLUGIN_DIRECTORY")!,
            Environment.GetEnvironmentVariable("OSCOVA_KOKORO_MODEL_DIRECTORY")!);
        var builder = new AgentPluginBuilder();
        new LocalSpeechSynthesisAgentPlugin().Configure(builder);
        var provider = Assert.Single(builder.SpeechSynthesisProviders).Factory(context);
        using var lifetime = (IDisposable)provider;
        var config = new SpeechProviderConfig();
        await provider.StartSessionAsync("test", config, null, "af_heart");
        var warmup = await provider.SynthesizeAsync("test", config, null, new SpeechSynthesisRequest { Text = "Hello." });
        Assert.True(warmup.TotalSamples > 0);

        var pending = provider.SynthesizeAsync("test", config, null, new SpeechSynthesisRequest
        {
            Text = string.Join(' ', Enumerable.Repeat("This is a local synthesis lifecycle check.", 10))
        });
        Assert.False(pending.IsCompleted);
        await provider.StopSessionAsync("test", config).WaitAsync(TimeSpan.FromSeconds(60));
        var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.True(error is null or OperationCanceledException, error?.ToString());
    }

    private sealed class MissingCredentials : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ResolveAsync(string key, string field, CancellationToken ct = default) => ResolveAsync(key, ct);
    }

    private sealed class KokoroFactAttribute : FactAttribute
    {
        public KokoroFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OSCOVA_KOKORO_MODEL_DIRECTORY")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OSCOVA_KOKORO_PLUGIN_DIRECTORY")))
            {
                Skip = "Set OSCOVA_KOKORO_MODEL_DIRECTORY to a folder containing kokoro.onnx and OSCOVA_KOKORO_PLUGIN_DIRECTORY to a root containing packages/kokorosharp/0.8.4/content.";
            }
        }
    }
}
