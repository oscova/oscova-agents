using System.Net;
using System.Text.Json;
using Oscova.Agents.Plugins.OpenAI;
using Oscova.Agents.Plugins.Tests.TestSupport;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Plugins.Tests.OpenAI;

/// <summary>Checks speech samples and metadata through the real SDK transport.</summary>
public sealed class OpenAiSpeechTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synthesis_PreservesPcmSamplesWithOptionalWavHeader(bool includeHeader)
    {
        byte[] pcm = [1, 0, 2, 0, 3, 0, 4, 0];
        using var http = new HttpClient(new StubHttpMessageHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var format = body.RootElement.GetProperty("response_format").GetString();
            Assert.Equal("pcm", format);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) };
        }));
        using var provider = new OpenAiSpeechSynthesisProvider(http);

        var response = await provider.SynthesizeAsync("user", new SpeechProviderConfig(), "test-key",
            new SpeechSynthesisRequest { Text = "Hello", IncludeWavHeader = includeHeader });

        Assert.Equal(24000, response.SampleRate);
        Assert.Equal(1, response.Channels);
        Assert.Equal(16, response.BitsPerSample);
        Assert.Equal(pcm.Length / 2, response.TotalSamples);
        if (includeHeader)
        {
            Assert.Equal("RIFF"u8.ToArray(), response.Audio[..4]);
            Assert.Equal(pcm, response.Audio[^pcm.Length..]);
        }
        else { Assert.Equal(pcm, response.Audio); }
    }
}
