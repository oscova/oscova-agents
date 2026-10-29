using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Net;
using Oscova.Agents.Policies;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>
/// Fetches web pages for agents as bounded markdown while relying on the named web__fetch client for SSRF protection.
/// </summary>
public sealed class WebToolset : IAgentToolset
{
    private const int MaxRedirects = 5;
    private const long MaxDownloadBytes = 5 * 1024 * 1024;
    private static readonly ReverseMarkdown.Converter MarkdownConverter = new(new ReverseMarkdown.Config
    {
        GithubFlavored = true,
        Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass },
        Links = { SmartHref = true },
        Formatting = { RemoveComments = true }
    });

    private readonly HttpClient _httpClient;
    private readonly ILogger<WebToolset> _logger;
    private readonly IToolOutputStore _outputStore;

    /// <summary>
    /// Creates a web__fetch toolset around the SSRF-safe named client registered by infrastructure.
    /// </summary>
    public WebToolset(
        HttpClient httpClient,
        ILogger<WebToolset> logger,
        IToolOutputStore outputStore)
    {
        _httpClient = httpClient;
        _logger = logger;
        _outputStore = outputStore;
    }

    /// <summary>
    /// Fetches an http or https URL and returns a bounded content slice for the agent.
    /// </summary>
    [DisplayName("fetch")]
    [AgentTool]
    [Description("Fetch a web page by URL and return its readable content as markdown.\nAccepts HTML and text pages (text/*). application/json responses are unsupported.\nLong pages show the start and save the full page to a file you can read or search.")]
    [ToolNetworkScope(NetworkScope.Egress)]
    public async Task<string> WebFetchAsync(
        [Description("The http/https URL to fetch.")] string url,
        [Description("Return raw HTML instead of cleaned markdown. Default false.")] bool raw = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateAllowedUri(url, out var uri, out var validationMessage))
        {
            return validationMessage;
        }

        try
        {
            // Headers-only completion must not release the deadline before the body is read.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_httpClient.Timeout);
            string content = await FetchDocumentAsync(uri, raw, deadline.Token).ConfigureAwait(false);
            return $"URL: {uri}\n\n" + ToolOutput.LimitText(_outputStore, "web", content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SsrfBlockedException || ex.InnerException is SsrfBlockedException)
        {
            throw new ToolFailureException(
                "That URL resolves to an address web__fetch is not allowed to contact. Local and private-network URLs are blocked by default. Unspecified, link-local, multicast, and other non-routable addresses stay blocked.",
                "An admin can allow trusted local and private URLs under Settings > Network.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "web__fetch failed for URL '{Url}'.", url);
            throw new ToolFailureException(
                "The page could not be fetched.",
                "Check the URL, or try a different source.");
        }
    }

    private async Task<string> FetchDocumentAsync(Uri uri, bool raw, CancellationToken ct)
    {
        var current = uri;
        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount == MaxRedirects)
                {
                    return $"Fetch failed: too many redirects from {uri}.";
                }

                var location = response.Headers.Location;
                if (location is null)
                {
                    return $"Fetch failed: redirect from {current} did not include a Location header.";
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!TryCreateAllowedUri(next.AbsoluteUri, out current, out var validationMessage))
                {
                    return validationMessage;
                }

                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Fetch failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == false)
            {
                return $"Unsupported content type '{mediaType}'. web__fetch handles HTML and text pages; use the PDF tool for PDFs.";
            }

            var body = await ReadCappedBodyAsync(response.Content, ct).ConfigureAwait(false);
            if (!raw && IsHtmlMediaType(mediaType))
            {
                body = MarkdownConverter.Convert(body);
            }

            return body;
        }

        return $"Fetch failed: too many redirects from {uri}.";
    }

    private static async Task<string> ReadCappedBodyAsync(HttpContent content, CancellationToken ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
        var scratch = new byte[81920];
        long remaining = MaxDownloadBytes;

        while (remaining > 0)
        {
            var readSize = (int)Math.Min(scratch.Length, remaining);
            var read = await stream.ReadAsync(scratch.AsMemory(0, readSize), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(scratch, 0, read);
            remaining -= read;
        }

        return GetEncoding(content).GetString(buffer.ToArray());
        }
    }

    private static Encoding GetEncoding(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
        catch (NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static bool TryCreateAllowedUri(string url, out Uri uri, out string message)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
        {
            message = "URL is required. Provide an absolute http or https URL.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            message = "Invalid URL. Provide an absolute http or https URL.";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            message = "Unsupported URL scheme. web__fetch only supports http and https URLs.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            message = "Unsupported URL. web__fetch does not accept URLs with embedded credentials.";
            return false;
        }

        uri = parsed;
        message = string.Empty;
        return true;
    }

    private static bool IsHtmlMediaType(string? mediaType) =>
        string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var value = (int)statusCode;
        return value is >= 300 and <= 399;
    }

}
