using Oscova.Agents.Llm;
using Oscova.Agents.Speech;
using System.Text.Json;

namespace Oscova.Agents.Tests.Framework.Llm;

public sealed class ProviderConfigurationTests
{
    /// <summary>Editing a copied draft cannot alter the original definition or weaken key comparison.</summary>
    [Fact]
    public void Options_CloneOwnsDictionaryAndComparesOrdinally()
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["gpuLayers"] = "12" };
        var original = new ModelProviderConfig { Options = options };
        var clone = original.Clone();
        options["gpuLayers"] = "24";

        Assert.Equal("12", clone.Options!["gpuLayers"]);
        Assert.False(original.EqualsConfiguration(clone));
        Assert.False(ModelProviderConfig.AreOptionsEqual(options, new Dictionary<string, string> { ["GpuLayers"] = "24" }));
    }

    /// <summary>Provider options survive cloning and affect configuration identity with exact keys and values.</summary>
    [Theory]
    [InlineData("{}", "null", true)]
    [InlineData("{\"gpuLayers\":\"12\",\"mode\":\"fast\"}", "{\"mode\":\"fast\",\"gpuLayers\":\"12\"}", true)]
    [InlineData("{\"gpuLayers\":\"12\"}", "{\"gpuLayers\":\"13\"}", false)]
    [InlineData("{\"gpuLayers\":\"12\"}", "{\"GpuLayers\":\"12\"}", false)]
    [InlineData("{\"mode\":\"fast\"}", "{\"mode\":\"Fast\"}", false)]
    public void Options_RoundTripAndCompare(string leftOptions, string rightOptions, bool equal)
    {
        var id = Guid.NewGuid();
        var left = JsonSerializer.Deserialize<ModelProviderConfig>($$"""{"Id":"{{id}}","Options":{{leftOptions}}}""")!;
        var right = JsonSerializer.Deserialize<ModelProviderConfig>($$"""{"Id":"{{id}}","Options":{{rightOptions}}}""")!;

        Assert.Equal(equal, left.EqualsConfiguration(right));
        using var cloned = JsonDocument.Parse(JsonSerializer.Serialize(left.Clone()));
        using var expected = JsonDocument.Parse(leftOptions);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, cloned.RootElement.GetProperty("Options")));
    }

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
