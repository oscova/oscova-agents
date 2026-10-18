using Oscova.Agents.Llm;
using Oscova.Agents.Speech;

namespace Oscova.Agents.Tests.Framework.Llm;

public sealed class ProviderConfigurationTests
{
    [Theory]
    [InlineData(false, "TeamA/v1", true)]
    [InlineData(false, "teama/v1", false)]
    [InlineData(false, "TeamA/v1?key=Token", false)]
    [InlineData(true, "TeamA/v1", true)]
    [InlineData(true, "teama/v1", false)]
    [InlineData(true, "TeamA/v1?key=Token", false)]
    public void EqualsConfiguration_DistinguishesEndpointResources(bool speech, string resource, bool expected)
    {
        const string endpoint = "https://example.test/TeamA/v1";
        if (speech)
        {
            var original = new SpeechProviderConfig { BaseUrl = endpoint };
            var changed = original.Clone();
            changed.BaseUrl = "https://example.test/" + resource;
            Assert.Equal(expected, original.EqualsConfiguration(changed));
        }
        else
        {
            var original = new ModelProviderConfig { BaseUrl = endpoint };
            var changed = original.Clone();
            changed.BaseUrl = "https://example.test/" + resource;
            Assert.Equal(expected, original.EqualsConfiguration(changed));
        }
    }
}
