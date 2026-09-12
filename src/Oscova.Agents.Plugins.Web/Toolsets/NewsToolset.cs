using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Credentials;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Plugins.Web.Toolsets;

/// <summary>
/// Exposes GNews search and headline tools backed by a user-scoped API key credential.
/// </summary>
[CredentialSlot(
    Key = "gnews-api-key",
    Label = "News",
    Description = "API key used by the news_search tool.",
    EnvVariable = "GNEWS_API_KEY")]
public sealed class NewsToolset : ICrewToolset
{
    private const string CredentialKey = "gnews-api-key";
    private const string BaseUrl = "https://gnews.io/api/v4/";
    private const string MissingCredentialMessage = "GNews is not configured. Add the News credential in Settings -> Credentials.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IToolCredentialResolver _credentialResolver;
    private readonly HttpClient _httpClient;
    private readonly ILogger<NewsToolset> _logger;
    private readonly Func<TimeZoneInfo> _userTimeZoneResolver;

    public NewsToolset(
        IToolCredentialResolver credentialResolver,
        HttpClient httpClient,
        ILogger<NewsToolset> logger,
        Func<TimeZoneInfo> userTimeZoneResolver)
    {
        _credentialResolver = credentialResolver;
        _httpClient = httpClient;
        _logger = logger;
        _userTimeZoneResolver = userTimeZoneResolver;
    }

    /// <summary>
    /// Searches recent GNews articles by keyword and returns a concise list of matches.
    /// </summary>
    [DisplayName("news_search")]
    [Description("""
        Searches recent news articles through GNews.
        Returns article title, source, published time, short summary, and the article URL.
        Use this when the user wants topic-driven article discovery rather than general headlines.
        """)]
    [ToolNetworkScope(NetworkScope.Egress)]
    public Task<string> SearchAsync(
        [Description("Search keywords or phrase.")] string query,
        [Description("Two-letter language code. Defaults to 'en'.")] string language = "en",
        [Description("Optional two-letter country code such as 'us' or 'in'.")] string? country = null,
        [Description("Maximum number of articles to return (default 10, max 25).")] int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromException<string>(new ToolFailureException("A news search query is required."));
        }

        maxResults = Math.Clamp(maxResults, 1, 25);

        var queryParameters = new List<KeyValuePair<string, string>>
        {
            new("q", query.Trim()),
            new("lang", NormalizeOptionalToken(language) ?? "en"),
            new("max", maxResults.ToString(CultureInfo.InvariantCulture))
        };

        var normalizedCountry = NormalizeOptionalToken(country);
        if (!string.IsNullOrWhiteSpace(normalizedCountry))
        {
            queryParameters.Add(new("country", normalizedCountry));
        }

        return ExecuteAsync("search", queryParameters, "news_search");
    }

    /// <summary>
    /// Fetches top GNews headlines for a category or country.
    /// </summary>
    [DisplayName("news_get_top_headlines")]
    [Description("""
        Fetches top headlines from GNews.
        Returns article title, source, published time, short summary, and the article URL.
        Use this when the user wants current headline-style coverage rather than keyword search.
        Categories include: general, world, nation, business, technology, entertainment, sports, science, health.
        """)]
    [ToolNetworkScope(NetworkScope.Egress)]
    public Task<string> GetTopHeadlinesAsync(
        [Description("Optional news category. Defaults to 'general'.")] string? category = null,
        [Description("Optional two-letter country code such as 'us' or 'in'.")] string? country = null,
        [Description("Two-letter language code. Defaults to 'en'.")] string language = "en",
        [Description("Maximum number of articles to return (default 10, max 25).")] int maxResults = 10)
    {
        maxResults = Math.Clamp(maxResults, 1, 25);

        var queryParameters = new List<KeyValuePair<string, string>>
        {
            new("category", NormalizeOptionalToken(category) ?? "general"),
            new("lang", NormalizeOptionalToken(language) ?? "en"),
            new("max", maxResults.ToString(CultureInfo.InvariantCulture))
        };

        var normalizedCountry = NormalizeOptionalToken(country);
        if (!string.IsNullOrWhiteSpace(normalizedCountry))
        {
            queryParameters.Add(new("country", normalizedCountry));
        }

        return ExecuteAsync("top-headlines", queryParameters, "news_get_top_headlines");
    }

    private async Task<string> ExecuteAsync(
        string endpoint,
        List<KeyValuePair<string, string>> queryParameters,
        string operationName)
    {
        try
        {
            var apiKey = await _credentialResolver.ResolveAsync(CredentialKey).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ToolFailureException(MissingCredentialMessage);
            }

            queryParameters.Add(new("apikey", apiKey.Trim()));
            var requestUri = $"{BaseUrl}{endpoint}?{BuildQueryString(queryParameters)}";
            using var response = await _httpClient.GetAsync(requestUri).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw MapError(response.StatusCode, responseBody, operationName);
            }

            var payload = JsonSerializer.Deserialize<GNewsResponse>(responseBody, JsonOptions);
            if (payload?.Articles is null || payload.Articles.Count == 0)
            {
                return "No news articles were found.";
            }

            return FormatArticles(payload.Articles);
        }
        catch (ToolFailureException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "GNews returned an unusable response for '{OperationName}'.", operationName);
            throw new ToolFailureException("GNews returned an unusable response.", "Try again later.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute GNews operation '{OperationName}'.", operationName);
            throw;
        }
    }

    private ToolFailureException MapError(HttpStatusCode statusCode, string responseBody, string operationName)
    {
        _logger.LogError(
            "GNews request failed for '{OperationName}'. Status: {StatusCode}. Body: {Body}",
            operationName,
            (int)statusCode,
            responseBody);

        switch (statusCode)
        {
            case HttpStatusCode.BadRequest:
                return new ToolFailureException("GNews rejected the request parameters.", "Check the search options and try again.");
            case HttpStatusCode.Unauthorized:
                return new ToolFailureException(
                    "The configured GNews credential is invalid.",
                    "Update the News credential in Settings > Credentials.");
            case HttpStatusCode.Forbidden:
                return new ToolFailureException("GNews denied the request.", "Check the credential or request options.");
            case (HttpStatusCode)429:
                return new ToolFailureException("The GNews rate limit or quota was reached.", "Try again later.");
            default:
                if ((int)statusCode >= 500)
                {
                    return new ToolFailureException("GNews is temporarily unavailable.", "Try again later.");
                }

                return new ToolFailureException("GNews could not complete the request.", "Check the search options or try again later.");
        }
    }

    private string FormatArticles(IReadOnlyList<GNewsArticle> articles)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Showing {articles.Count} article(s):")
            .AppendLine();

        for (var index = 0; index < articles.Count; index++)
        {
            var article = articles[index];
            builder.AppendLine($"[{index + 1}] Title: {article.Title ?? "(untitled)"}")
                .AppendLine($"    Source: {article.Source?.Name ?? "(unknown source)"}");

            var publishedAt = FormatPublishedAt(article.PublishedAt);
            if (!string.IsNullOrWhiteSpace(publishedAt))
            {
                builder.AppendLine($"    Published: {publishedAt}");
            }

            if (!string.IsNullOrWhiteSpace(article.Description))
            {
                builder.AppendLine($"    Summary: {WebText.ToSingleLine(article.Description, 240)}");
            }

            builder.AppendLine($"    URL: {article.Url ?? "(missing url)"}")
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        return string.Join(
            "&",
            parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private static string? NormalizeOptionalToken(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private string? FormatPublishedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmedValue = value.Trim();
        if (!DateTimeOffset.TryParse(
            trimmedValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed))
        {
            return trimmedValue;
        }

        return TimeZoneInfo
            .ConvertTime(parsed, _userTimeZoneResolver())
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private sealed class GNewsResponse
    {
        [JsonPropertyName("totalArticles")]
        public int TotalArticles { get; set; }

        [JsonPropertyName("articles")]
        public List<GNewsArticle> Articles { get; set; } = [];
    }

    private sealed class GNewsArticle
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("image")]
        public string? Image { get; set; }

        [JsonPropertyName("publishedAt")]
        public string? PublishedAt { get; set; }

        [JsonPropertyName("lang")]
        public string? Lang { get; set; }

        [JsonPropertyName("source")]
        public GNewsSource? Source { get; set; }
    }

    private sealed class GNewsSource
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }
    }

}
