using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Credentials;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Plugins.Web.Toolsets;

/// <summary>
/// Exposes LangSearch web search through a user-scoped API key credential.
/// </summary>
[CredentialSlot(
    Key = "langsearch",
    Label = "Web Search",
    Description = "API key used by the web_search tool.",
    EnvVariable = "LANGSEARCH_API_KEY")]
public sealed class WebSearchToolset(
    IToolCredentialResolver credentialResolver,
    HttpClient httpClient,
    ILogger<WebSearchToolset> logger) : ICrewToolset
{
    public const string CredentialKey = "langsearch";

    /// <summary>
    /// Searches the web and returns the provider response as compact JSON.
    /// </summary>
    [DisplayName("web_search")]
    [Description("Search the web for current information. Returns titles, URLs, and short snippets; set include_summary for longer per-result summaries.")]
    [ToolNetworkScope(NetworkScope.Egress)]
    public async Task<string> WebSearchAsync(
        [Description("The search query.")] string query,
        [Description("Number of results to return (1-20). Ask for more only if the first page is not enough.")] int count = 5,
        [Description("Freshness filter: noLimit, day, week, month.")] string freshness = "noLimit",
        [Description("Include a longer per-result summary. Heavier output; leave false unless you need depth.")] bool include_summary = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ToolFailureException("A web search query is required.");
        }

        count = Math.Clamp(count, 1, 20);
        if (!IsSupportedFreshness(freshness))
        {
            throw new ToolFailureException("The freshness filter is invalid.", "Use noLimit, day, week, or month.");
        }

        try
        {
            var apiKey = await credentialResolver.ResolveAsync(CredentialKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ToolFailureException(
                    "Web search is not configured.",
                    "Add the Web Search credential in Settings > Credentials.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.langsearch.com/v1/web-search");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // Only ask the provider to generate summaries when the caller wants them; they are the heaviest field.
            var payload = JsonSerializer.Serialize(new { query, count, freshness, summary = include_summary });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Web search failed with HTTP status {StatusCode}.", (int)response.StatusCode);
                throw new ToolFailureException("The web search request failed.", "Check the credential or try again later.");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (stream.ConfigureAwait(false))
                {
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (!doc.RootElement.TryGetProperty("data", out var dataElement) ||
                        !dataElement.TryGetProperty("webPages", out var webPagesElement) ||
                        !webPagesElement.TryGetProperty("value", out var valuesElement))
                    {
                        throw new ToolFailureException("Web search returned an unusable response.", "Try again later.");
                    }

                    var results = valuesElement
                        .EnumerateArray()
                        .Select(item => new
                        {
                            title = item.TryGetProperty("name", out var titleElement) ? titleElement.GetString() : null,
                            url = item.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null,
                            siteName = item.TryGetProperty("siteName", out var siteElement) ? siteElement.GetString() : null,
                            snippet = CapSnippet(item),
                            summary = include_summary ? CapSummary(item) : null,
                            date = item.TryGetProperty("datePublished", out var dateElement) ? dateElement.GetString() : null
                        })
                        .ToList();

                    return JsonSerializer.Serialize(new { query, total = results.Count, results });
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                logger.LogError(ex, "Web search returned an unusable response.");
                throw new ToolFailureException("Web search returned an unusable response.", "Try again later.");
            }
        }
        catch (ToolFailureException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Web search failed unexpectedly.");
            throw;
        }
    }

    private static bool IsSupportedFreshness(string freshness)
    {
        return freshness is "noLimit" or "day" or "week" or "month";
    }

    // Keep each result light: a short snippet is enough for the model to decide relevance.
    private static string? CapSnippet(JsonElement item)
    {
        return item.TryGetProperty("snippet", out var snippetElement) && snippetElement.GetString() is { } snippet
            ? WebText.ToSingleLine(snippet, 240)
            : null;
    }

    private static string? CapSummary(JsonElement item)
    {
        return item.TryGetProperty("summary", out var summaryElement) && summaryElement.GetString() is { } summary
            ? WebText.ToSingleLine(summary, 500)
            : null;
    }
}
