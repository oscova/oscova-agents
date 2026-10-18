using Oscova.Agents.Llm;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Tests.Framework.Policies;

public sealed class EgressClassifierTests
{
    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://192.168.1.20:11434", true)]
    [InlineData("http://10.0.0.5:11434", true)]
    [InlineData("http://172.16.0.5:11434", true)]
    [InlineData("http://172.31.255.5:11434", true)]
    [InlineData("http://169.254.1.5:11434", true)]
    [InlineData("http://[::ffff:192.168.1.20]:11434", true)]
    [InlineData("http://[::ffff:10.0.0.5]:11434", true)]
    [InlineData("http://[::ffff:172.16.0.5]:11434", true)]
    [InlineData("http://[::ffff:169.254.1.5]:11434", true)]
    [InlineData("http://[::ffff:8.8.8.8]", false)]
    [InlineData("http://[::ffff:172.32.0.5]", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("https://api.openai.com", false)]
    public void IsLocalHost_ClassifiesExpectedAddresses(string uri, bool expected)
    {
        Assert.Equal(expected, EgressClassifier.IsLocalHost(new Uri(uri)));
    }

    [Fact]
    public void IsLocalHost_RelativeUri_ReturnsFalse()
    {
        Assert.False(EgressClassifier.IsLocalHost(new Uri("/v1/chat", UriKind.Relative)));
    }

    [Fact]
    public void Classify_ModelProvider_UsesProviderDefaultsAndConfiguredEndpoint()
    {
        Assert.Equal(EgressClass.Local, EgressClassifier.Classify(ModelProviderKeys.Local, null, null));
        Assert.Equal(EgressClass.Local, EgressClassifier.Classify(ModelProviderKeys.Ollama, null, "http://localhost:11434/"));
        Assert.Equal(EgressClass.Local, EgressClassifier.Classify(ModelProviderKeys.OpenAICompatible, "http://localhost:1234/v1/", "https://api.openai.com/v1/"));
        Assert.Equal(EgressClass.Local, EgressClassifier.Classify(ModelProviderKeys.OpenAICompatible, "http://192.168.1.50:1234/v1/", "https://api.openai.com/v1/"));
        Assert.Equal(EgressClass.Cloud, EgressClassifier.Classify(ModelProviderKeys.OpenAI, null, null));
        Assert.Equal(EgressClass.Cloud, EgressClassifier.Classify(ModelProviderKeys.Anthropic, null, "https://api.anthropic.com/"));
        Assert.Equal(EgressClass.Cloud, EgressClassifier.Classify("Acme", null, null));
        Assert.Equal(EgressClass.Local, EgressClassifier.Classify("FoundryLocal", null, null, defaultIsLocalEndpoint: true));
        Assert.Equal(EgressClass.Cloud, EgressClassifier.Classify("FoundryLocal", null, null, false, true));
    }

    [Theory]
    [InlineData(true, EgressClass.Local)]
    [InlineData(false, EgressClass.Cloud)]
    public void Classify_WhenEndpointOverrideIsSet_UsesOverride(bool isLocalEndpoint, EgressClass expected)
    {
        Assert.Equal(
            expected,
            EgressClassifier.Classify(
                ModelProviderKeys.OpenAICompatible,
                "https://api.example.com/v1/",
                null,
                isLocalEndpoint));
    }

    [Theory]
    [InlineData(null, null, EgressClass.Local)]
    [InlineData(null, "http://localhost:5000/", EgressClass.Local)]
    [InlineData(null, "http://192.168.1.50:5000/", EgressClass.Local)]
    [InlineData(null, "http://8.8.8.8/", EgressClass.Cloud)]
    [InlineData("http://127.0.0.1:5000/", "http://8.8.8.8/", EgressClass.Local)]
    [InlineData("http://10.0.0.5:5000/", "http://8.8.8.8/", EgressClass.Local)]
    [InlineData("http://8.8.8.8/", "http://localhost:5000/", EgressClass.Cloud)]
    [InlineData("not-an-endpoint", "http://localhost:5000/", EgressClass.Cloud)]
    [InlineData(null, "not-an-endpoint", EgressClass.Cloud)]
    public void ClassifySpeech_UsesConfiguredEndpointThenProviderDefault(
        string? baseUrl,
        string? defaultEndpoint,
        EgressClass expected)
    {
        Assert.Equal(expected, EgressClassifier.ClassifySpeech(baseUrl, defaultEndpoint));
    }
}
