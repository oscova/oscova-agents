using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.ApiContracts.Projects;
using System.Net;

namespace Oscova.Agents.Host.Services.Api;

public abstract class ApiClientBase
{
    /// <summary>Retains a revision conflict as a recoverable result instead of a failed transport.</summary>
    protected static async Task<ProjectFileSaveResult> SaveTextAsync(HttpClient client, string route,
        SaveProjectFileRequest request, CancellationToken ct)
    {
        using var response = await client.PutAsJsonAsync(route, request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Conflict) { await EnsureSuccessAsync(response).ConfigureAwait(false); }

        return await response.Content.ReadFromJsonAsync<ProjectFileSaveResult>(ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The server returned no confirmed file save result.");
    }
    private static readonly System.Text.Json.JsonSerializerOptions _caseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await TryReadErrorAsync(response).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException(error);
        }

        response.EnsureSuccessStatusCode();
    }

    private static async Task<string?> TryReadErrorAsync(HttpResponseMessage response)
    {
        // An unhandled server error renders the HTML error page, which is not a message for the operator.
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text) || response.Content.Headers.ContentType?.MediaType == "text/html")
        {
            return null;
        }

        try
        {
            var dto = System.Text.Json.JsonSerializer.Deserialize<ErrorResult>(text, _caseInsensitive);
            if (!string.IsNullOrWhiteSpace(dto?.Message))
            {
                return dto.Message;
            }

            // A ProblemDetails body without a detail, such as a bare NotFound, still has a readable title.
            using var document = System.Text.Json.JsonDocument.Parse(text);
            foreach (var property in new[] { "detail", "title" })
            {
                if (document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return value.GetString();
                }
            }

            return text;
        }
        catch
        {
            return text;
        }
    }
}
