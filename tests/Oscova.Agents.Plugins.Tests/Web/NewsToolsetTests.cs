using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Harness;
using Oscova.Agents.Plugins.Tests.TestSupport;
using Oscova.Agents.Plugins.Web.Toolsets;

namespace Oscova.Agents.Plugins.Tests.Web;

public sealed class NewsToolsetTests
{
    [Fact]
    public async Task SearchAsync_WithoutCredential_ThrowsSetupGuidance()
    {
        var toolset = CreateToolset(
            credentialValue: null,
            responseFactory: _ => new HttpResponseMessage(HttpStatusCode.OK));

        var exception = await Assert.ThrowsAsync<ToolFailureException>(() => toolset.SearchAsync("agents"));

        Assert.Equal("GNews is not configured. Add the News credential in Settings -> Credentials.", exception.Message);
    }

    [Fact]
    public async Task SearchAsync_WithSuccessfulResponse_FormatsArticles()
    {
        const string responseJson = """
            {
              "totalArticles": 1,
              "articles": [
                {
                  "id": "article-1",
                  "title": "Agent platforms keep expanding",
                  "description": "A concise summary of the article.",
                  "url": "https://example.com/agent-platforms",
                  "image": "https://example.com/image.jpg",
                  "publishedAt": "2026-04-04T09:30:00Z",
                  "lang": "en",
                  "source": {
                    "id": "source-1",
                    "name": "The Verge",
                    "url": "https://theverge.com",
                    "country": "us"
                  }
                }
              ]
            }
            """;

        var userTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "TestUserTimeZone",
            TimeSpan.FromHours(5.5),
            "Test User Time Zone",
            "Test User Time Zone");
        var toolset = CreateToolset(
            credentialValue: "secret-key",
            responseFactory: _ => CreateJsonResponse(HttpStatusCode.OK, responseJson),
            userTimeZoneResolver: () => userTimeZone);

        var result = await toolset.SearchAsync("agents", maxResults: 5);

        Assert.Contains("Showing 1 article(s):", result);
        Assert.Contains("Title: Agent platforms keep expanding", result);
        Assert.Contains("Source: The Verge", result);
        Assert.Contains("Published: 2026-04-04 15:00", result);
        Assert.Contains("Summary: A concise summary of the article.", result);
        Assert.Contains("URL: https://example.com/agent-platforms", result);

        userTimeZone = TimeZoneInfo.Utc;
        result = await toolset.SearchAsync("agents", maxResults: 5);

        Assert.Contains("Published: 2026-04-04 09:30", result);
    }

    [Fact]
    public async Task GetTopHeadlinesAsync_WithUnauthorizedResponse_ThrowsCredentialMessage()
    {
        const string responseJson = """
            {
              "errors": [
                "API key is invalid."
              ]
            }
            """;

        var toolset = CreateToolset(
            credentialValue: "bad-key",
            responseFactory: _ => CreateJsonResponse(HttpStatusCode.Unauthorized, responseJson));

        var exception = await Assert.ThrowsAsync<ToolFailureException>(() => toolset.GetTopHeadlinesAsync());

        Assert.Equal("The configured GNews credential is invalid.", exception.Message);
        Assert.Equal("Update the News credential in Settings > Credentials.", exception.Fix);
        Assert.DoesNotContain("API key is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTopHeadlinesAsync_WithRateLimitResponse_ThrowsQuotaMessage()
    {
        const string responseJson = """
            {
              "errors": [
                "Rate limit exceeded."
              ]
            }
            """;

        var toolset = CreateToolset(
            credentialValue: "secret-key",
            responseFactory: _ => CreateJsonResponse((HttpStatusCode)429, responseJson));

        var exception = await Assert.ThrowsAsync<ToolFailureException>(() => toolset.GetTopHeadlinesAsync(category: "technology"));

        Assert.Equal("The GNews rate limit or quota was reached.", exception.Message);
        Assert.Equal("Try again later.", exception.Fix);
    }

    private static NewsToolset CreateToolset(
        string? credentialValue,
        Func<Uri, HttpResponseMessage> responseFactory,
        Func<TimeZoneInfo>? userTimeZoneResolver = null)
    {
        var credentialResolver = new TestToolCredentialResolver(credentialValue);
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var requestUri = request.RequestUri ?? throw new InvalidOperationException("Request URI was not set.");
            return responseFactory(requestUri);
        }), disposeHandler: true);
        var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = loggerFactory.CreateLogger<NewsToolset>();
        var userTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "TestUserTimeZone",
            TimeSpan.FromHours(5.5),
            "Test User Time Zone",
            "Test User Time Zone");

        return new NewsToolset(credentialResolver, httpClient, logger, userTimeZoneResolver ?? (() => userTimeZone));
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
