using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Oscova.Agents.App.Cli;

public sealed class HostApiClient(IHttpClientFactory httpClientFactory)
{
    public HttpClient Create(ResolvedHost host)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(host.HostUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BuildUserAgent());
        if (!string.IsNullOrWhiteSpace(host.Token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
        }

        return client;
    }

    /// <summary>
    /// Creates a loopback lifecycle client authorized by the runtime control token.
    /// </summary>
    public HttpClient CreateLocalControl(string hostUrl, string controlToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(hostUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BuildUserAgent());
        client.DefaultRequestHeaders.Add("X-Oscova-Control-Token", controlToken);
        return client;
    }

    internal static string BuildUserAgent()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";
        return $"Oscova-Cli/{version}";
    }
}

/// <summary>
/// Maps host HTTP failures onto the single CLI exit-code contract.
/// </summary>
public static class CliHttpErrors
{
    private const int MaxBodyLength = 400;
    private static readonly string[] MessagePropertyNames = ["message", "Message", "title", "detail"];

    /// <summary>
    /// Throws a <see cref="CliExitException"/> when the response is not a success status.
    /// </summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw await ToCliExceptionAsync(response, operation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the exit-coded exception for a failed host response.
    /// </summary>
    public static async Task<CliExitException> ToCliExceptionAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var detail = await ReadDetailAsync(response, cancellationToken).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"{operation} failed with HTTP {(int)response.StatusCode}."
            : $"{operation} failed: {detail}";

        return new CliExitException(MapExitCode(response.StatusCode), message);
    }

    /// <summary>
    /// Maps an HTTP status onto an exit code: 401 to 2, 403 to 3, 400 and 409 to 4, everything else to 1.
    /// </summary>
    public static int MapExitCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => 2,
        HttpStatusCode.Forbidden => 3,
        HttpStatusCode.BadRequest or HttpStatusCode.Conflict => 4,
        _ => 1
    };

    private static async Task<string?> ReadDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // Host controllers answer with either an ErrorResult document or a bare string.
        var message = TryReadMessageProperty(body) ?? body;
        message = message.Trim();
        return message.Length > MaxBodyLength ? message[..MaxBodyLength] + "..." : message;
    }

    private static string? TryReadMessageProperty(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in MessagePropertyNames)
            {
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
