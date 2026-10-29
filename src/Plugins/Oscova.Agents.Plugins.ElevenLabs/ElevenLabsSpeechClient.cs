using ElevenLabs;
using Oscova.Agents.Speech;
using System.Net;

namespace Oscova.Agents.Plugins.ElevenLabs;

/// <summary>Keeps SDK credentials, endpoints and safe diagnostics local to each speech invocation.</summary>
internal static class ElevenLabsSpeechClient
{
    internal const string DefaultEndpoint = "https://api.elevenlabs.io/";

    /// <summary>Creates an invocation-owned SDK client over the Host's guarded transport.</summary>
    internal static ElevenLabsClient Create(Func<HttpClient> createHttpClient, SpeechProviderConfig config, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Select a populated ElevenLabs API-key credential in speech settings.");
        }

        var endpoint = new Uri(string.IsNullOrWhiteSpace(config.BaseUrl) ? DefaultEndpoint : config.BaseUrl, UriKind.Absolute);
        // A fresh guarded client prevents SDK BaseAddress and authorization mutations crossing users.
        var client = new ElevenLabsClient(apiKey, httpClient: createHttpClient(), baseUri: endpoint);
        // Retrying speech generation can consume credits after an uncertain response.
        client.Options.Retry.MaxAttempts = 1;
        return client;
    }

    /// <summary>Retains actionable service diagnostics without leaking the SDK response body.</summary>
    internal static HttpRequestException Failure(string operation, ApiException error)
    {
        var guidance = error.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Check the selected credential and its speech/voice permissions.",
            HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests => "Check account credits and service limits before trying again.",
            _ => "Check the selected model, voice and service availability before trying again."
        };
        // SDK exceptions contain response bodies, so neither their message nor inner exception can reach Host logs.
        return new HttpRequestException($"ElevenLabs {operation} failed (HTTP {(int)error.StatusCode}). {guidance}", null, error.StatusCode);
    }
}
