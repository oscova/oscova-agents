using System.Net;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Core.ApiContracts.Projects;

namespace Oscova.Agents.Host.Services.Api;

/// <summary>
/// Reads bounded textual artifact previews through the same authenticated routes used by browser-native previews.
/// </summary>
public sealed class ArtifactApiClient(HttpClient httpClient) : ApiClientBase
{
    /// <summary>Loads a complete editable snapshot through the opaque attributed file identity.</summary>
    public async Task<ProjectFilePreview> ReadEditableAsync(string threadId, string artifactId, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Chat.ArtifactEdit(threadId, artifactId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<ProjectFilePreview>(ct).ConfigureAwait(false))!;
    }

    /// <summary>Conditionally saves the original attributed file rather than an artifact copy.</summary>
    public Task<ProjectFileSaveResult> SaveEditableAsync(string threadId, string artifactId,
        SaveProjectFileRequest request, CancellationToken ct) =>
        SaveTextAsync(httpClient, ApiRoutes.Chat.ArtifactEdit(threadId, artifactId), request, ct);

    /// <summary>Checks current access before loading browser-native media or offering a download.</summary>
    public async Task<bool> IsAvailableAsync(string threadId, string artifactId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, ApiRoutes.Chat.ArtifactDownload(threadId, artifactId));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Reads Markdown or escaped text content without exposing a filesystem path to the component.
    /// </summary>
    public async Task<ArtifactTextPreviewResult> GetTextAsync(
        string threadId,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient
            .GetAsync(ApiRoutes.Chat.ArtifactContent(threadId, artifactId), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            return ArtifactTextPreviewResult.TooLarge;
        }

        if (!response.IsSuccessStatusCode)
        {
            return ArtifactTextPreviewResult.Unavailable;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new ArtifactTextPreviewResult(content, IsTooLarge: false, IsAvailable: true);
    }
}

/// <summary>
/// Describes whether bounded text content is available for inline rendering.
/// </summary>
public sealed record ArtifactTextPreviewResult(string? Content, bool IsTooLarge, bool IsAvailable)
{
    public static ArtifactTextPreviewResult TooLarge { get; } = new(null, IsTooLarge: true, IsAvailable: false);
    public static ArtifactTextPreviewResult Unavailable { get; } = new(null, IsTooLarge: false, IsAvailable: false);
}
