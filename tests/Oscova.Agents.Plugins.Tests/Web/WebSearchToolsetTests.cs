using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Harness;
using Oscova.Agents.Plugins.Tests.TestSupport;
using Oscova.Agents.Plugins.Web.Toolsets;

namespace Oscova.Agents.Plugins.Tests.Web;

public sealed class WebSearchToolsetTests
{
    [Fact]
    public async Task WebSearchAsync_WithoutCredential_ThrowsSetupGuidance()
    {
        var toolset = CreateToolset(null, _ => new HttpResponseMessage(HttpStatusCode.OK));

        var exception = await Assert.ThrowsAsync<ToolFailureException>(() => toolset.WebSearchAsync("agents"));

        Assert.Equal("Web search is not configured.", exception.Message);
        Assert.Equal("Add the Web Search credential in Settings > Credentials.", exception.Fix);
    }

    [Fact]
    public async Task WebSearchAsync_WithSuccessfulResponse_ReturnsSerializedResults()
    {
        const string responseJson = """
            {
              "data": {
                "webPages": {
                  "value": [
                    {
                      "name": "Oscova Agents",
                      "url": "https://example.com/oscova",
                      "siteName": "Example",
                      "snippet": "A local agent platform.",
                      "summary": "Summary text.",
                      "datePublished": "2026-05-24T10:00:00Z"
                    }
                  ]
                }
              }
            }
            """;

        var toolset = CreateToolset(
            "secret-key",
            _ => CreateJsonResponse(HttpStatusCode.OK, responseJson));

        var result = await toolset.WebSearchAsync("oscova", count: 1);

        Assert.Contains("Oscova Agents", result);
        Assert.Contains("https://example.com/oscova", result);
        Assert.Contains("\"total\":1", result);
    }

    [Fact]
    public async Task WebSearchAsync_DefaultsToSnippetsWithoutSummary()
    {
        string? requestBody = null;
        var toolset = CreateToolset(
            "secret-key",
            request =>
            {
                requestBody = request.Content!.ReadAsStringAsync().Result;
                return CreateJsonResponse(HttpStatusCode.OK, LongFieldsResponse());
            });

        var result = await toolset.WebSearchAsync("oscova");

        // The provider is not asked to generate summaries when the caller does not want them.
        Assert.Contains("\"summary\":false", requestBody);
        // The heavy summary is absent and the snippet is capped.
        Assert.DoesNotContain("LONG_SUMMARY_MARKER", result);
        Assert.Contains("...", result);
    }

    [Fact]
    public async Task WebSearchAsync_WithIncludeSummary_ReturnsCappedSummary()
    {
        var toolset = CreateToolset(
            "secret-key",
            _ => CreateJsonResponse(HttpStatusCode.OK, LongFieldsResponse()));

        var result = await toolset.WebSearchAsync("oscova", include_summary: true);

        Assert.Contains("LONG_SUMMARY_MARKER", result);
        Assert.Contains("...", result);
    }

    [Fact]
    public async Task WebSearchAsync_ClampsCountToTwenty()
    {
        string? requestBody = null;
        var toolset = CreateToolset(
            "secret-key",
            request =>
            {
                requestBody = request.Content!.ReadAsStringAsync().Result;
                return CreateJsonResponse(HttpStatusCode.OK, "{\"data\":{\"webPages\":{\"value\":[]}}}");
            });

        await toolset.WebSearchAsync("oscova", count: 50);

        Assert.Contains("\"count\":20", requestBody);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"data\":{\"webPages\":{\"value\":{}}}}")]
    public async Task WebSearchAsync_WithUnusableResponse_ThrowsSafeFailure(string responseJson)
    {
        var toolset = CreateToolset(
            "secret-key",
            _ => CreateJsonResponse(HttpStatusCode.OK, responseJson));

        var failure = await Assert.ThrowsAsync<ToolFailureException>(() => toolset.WebSearchAsync("oscova"));

        Assert.Equal("Web search returned an unusable response.", failure.Message);
        Assert.Equal("Try again later.", failure.Fix);
    }

    private static string LongFieldsResponse()
    {
        var snippet = "SNIP " + new string('a', 300);
        var summary = "LONG_SUMMARY_MARKER " + new string('b', 600);
        return $$"""
            {
              "data": {
                "webPages": {
                  "value": [
                    {
                      "name": "Oscova Agents",
                      "url": "https://example.com/oscova",
                      "siteName": "Example",
                      "snippet": "{{snippet}}",
                      "summary": "{{summary}}",
                      "datePublished": "2026-05-24T10:00:00Z"
                    }
                  ]
                }
              }
            }
            """;
    }

    private static WebSearchToolset CreateToolset(string? credentialValue, Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var credentialResolver = new TestToolCredentialResolver(credentialValue);
        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory), disposeHandler: true);
        return new WebSearchToolset(credentialResolver, httpClient, NullLogger<WebSearchToolset>.Instance);
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestToolCredentialResolver(string? value) : IToolCredentialResolver
    {
        public Task<string?> ResolveAsync(string credentialKey, string fieldName, CancellationToken ct = default)
        {
            return Task.FromResult(value);
        }

        public Task<string?> ResolveAsync(string credentialKey, CancellationToken ct = default)
        {
            return Task.FromResult(value);
        }
    }
}
