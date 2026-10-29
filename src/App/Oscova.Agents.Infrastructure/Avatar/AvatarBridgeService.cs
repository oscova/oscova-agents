using Microsoft.Extensions.Logging;
using Oscova.Agents.Avatar;
using Oscova.Agents.Avatar.Contracts;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Infrastructure.Avatar
{
    public partial class AvatarBridgeService : IAvatarBridgeService, IDisposable
    {
        internal const string EndpointClientName = "avatar-bridge";
        private readonly IHttpClientFactory _httpClientFactory;
        private HttpClient? _httpClient;
        private readonly Dictionary<string, AuthenticationHeaderValue?> _speechTargets = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ILogger<AvatarBridgeService> _logger;
        private readonly AvatarBridgeCredentialStore _credentialStore;
        private AvatarAccessMode? _activeAccessMode;
        private bool _credentialStale;
        private long _sessionVersion;

        public AvatarBridgeService(
            AvatarBridgeCredentialStore credentialStore,
            ILogger<AvatarBridgeService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = null
            };

            _credentialStore = credentialStore;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public string? ConnectedBaseUrl => _httpClient?.BaseAddress?.AbsoluteUri.TrimEnd('/');

        /// <inheritdoc />
        public long SessionVersion => Interlocked.Read(ref _sessionVersion);

        public async Task<AvatarBridgeOperationResult> ConnectAsync(
            string baseUrl,
            AvatarAccessMode accessMode = AvatarAccessMode.Shared)
        {
            var normalizedUrl = NormalizeBaseUrl(baseUrl);
            ArgumentOutOfRangeException.ThrowIfNotEqual(Enum.IsDefined(accessMode), true, nameof(accessMode));

            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await PrepareConnectionAsync(normalizedUrl, accessMode, requireActivation: true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        // The caller owns the setup gate, never the lifetime of an individual control request.
        private async Task<AvatarBridgeOperationResult> PrepareConnectionAsync(
            string normalizedUrl, AvatarAccessMode accessMode, bool requireActivation, CancellationToken cancellationToken)
        {
            try
            {
                if (_httpClient is not null &&
                    !_credentialStale &&
                    string.Equals(
                        ConnectedBaseUrl,
                        normalizedUrl,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!requireActivation) return Success("Avatar credential is loaded.");
                    var probe = await ProbeAsync(_httpClient, cancellationToken).ConfigureAwait(false);
                    if (probe.Success)
                    {
                        if (_activeAccessMode == accessMode)
                        {
                            return Success("Avatar Engine is connected.");
                        }

                        var changedMode = await ActivateAsync(_httpClient, accessMode, cancellationToken).ConfigureAwait(false);
                        if (changedMode.Success)
                        {
                            _activeAccessMode = accessMode;
                        }

                        return changedMode;
                    }

                    if (probe.ErrorCode == "activation_required")
                    {
                        var activation = await ActivateAsync(_httpClient, accessMode, cancellationToken).ConfigureAwait(false);
                        if (activation.Success)
                        {
                            _activeAccessMode = accessMode;
                            Interlocked.Increment(ref _sessionVersion);
                        }

                        return activation;
                    }

                    if (probe.ErrorCode != "pairing_required") return probe;
                    // Another process may have paired since this retained credential was loaded.
                    _credentialStale = true;
                }

                if (_httpClient is not null)
                {
                    _httpClient.Dispose();
                    _httpClient = null;
                    _activeAccessMode = null;
                    Interlocked.Increment(ref _sessionVersion);
                }

                HttpClient? candidate = _httpClientFactory.CreateClient(EndpointClientName);
                candidate.BaseAddress = new Uri(normalizedUrl, UriKind.Absolute);

                try
                {
                    _logger.LogInformation("Connecting to avatar bridge at {BaseUrl}", candidate.BaseAddress);
                    var identity = await GetIdentityAsync(candidate, cancellationToken).ConfigureAwait(false);
                    if (!identity.Result.Success || identity.EngineId is null)
                    {
                        return identity.Result;
                    }

                    var bridgeToken = await _credentialStore
                        .LoadTokenAsync(normalizedUrl, identity.EngineId.Value)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(bridgeToken))
                    {
                        return Failure(
                            "pairing_required",
                            "Pair this Oscova installation with the Avatar Engine.");
                    }

                    candidate.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", bridgeToken);

                    var connection = requireActivation
                        ? await ActivateAsync(candidate, accessMode, cancellationToken).ConfigureAwait(false)
                        : Success("Avatar credential is loaded.");

                    if (!connection.Success)
                    {
                        return connection;
                    }

                    _httpClient = candidate;
                    _activeAccessMode = requireActivation ? accessMode : null;
                    _credentialStale = false;
                    candidate = null;
                    Interlocked.Increment(ref _sessionVersion);

                    return Success("Avatar Engine is connected.");
                }
                finally
                {
                    candidate?.Dispose();
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Avatar bridge at {BaseUrl} returned an invalid response", normalizedUrl);
                return Failure("invalid_response", "The Avatar Engine returned an invalid response.");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to connect to avatar bridge at {BaseUrl}", normalizedUrl);
                return Failure("unreachable", "The Avatar Engine could not be reached.");
            }
        }

        public async Task<AvatarPairingStartResult> StartPairingAsync(string baseUrl)
        {
            var normalizedUrl = NormalizeBaseUrl(baseUrl);
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var client = _httpClientFactory.CreateClient(EndpointClientName);
                client.BaseAddress = new Uri(normalizedUrl);
                using var request = new HttpRequestMessage(HttpMethod.Post, "/avatarbridge/pairing/start");
                using var response = await SendBoundedAsync(client, request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await ReadErrorAsync(response).ConfigureAwait(false);
                    return new AvatarPairingStartResult
                    {
                        ErrorCode = error.ErrorCode,
                        Description = error.Description,
                        RetryAfterSeconds = error.RetryAfterSeconds
                    };
                }

                var body = await DeserializeAsync<PairingStartResponse>(response).ConfigureAwait(false);
                return body?.Success == true && body.ExpiresInSeconds > 0
                    ? new AvatarPairingStartResult
                    {
                        Success = true,
                        Description = body.Description,
                        ExpiresInSeconds = body.ExpiresInSeconds
                    }
                    : new AvatarPairingStartResult
                    {
                        ErrorCode = "invalid_response",
                        Description = "The Avatar Engine returned an invalid pairing response."
                    };
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Avatar Engine returned an invalid pairing response at {BaseUrl}", normalizedUrl);
                return new AvatarPairingStartResult
                {
                    ErrorCode = "invalid_response",
                    Description = "The Avatar Engine returned an invalid pairing response."
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to start Avatar Engine pairing at {BaseUrl}", normalizedUrl);
                return new AvatarPairingStartResult
                {
                    ErrorCode = "unreachable",
                    Description = "The Avatar Engine could not be reached."
                };
            }
            finally
            {
                _connectLock.Release();
            }
        }

        public async Task<AvatarBridgeOperationResult> CompletePairingAsync(string baseUrl, string pairingCode)
        {
            var clientName = $"Oscova Assistant - {Environment.MachineName}";
            if (clientName.Length > 100) clientName = clientName[..100];
            return ToOperationResult(await PairEndpointAsync(baseUrl, pairingCode, clientName, CancellationToken.None).ConfigureAwait(false));
        }

        private HttpClient HttpClient => _httpClient ?? throw new InvalidOperationException("Avatar bridge has not been connected.");

        private async Task<(Guid? EngineId, AvatarBridgeOperationResult Result)> GetIdentityAsync(HttpClient client, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/avatarbridge/identity");
            using var response = await SendBoundedAsync(client, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await ReadErrorAsync(response).ConfigureAwait(false));
            }

            var body = await DeserializeAsync<IdentityResponse>(response).ConfigureAwait(false);
            return body?.Success == true && Guid.TryParse(body.EngineId, out var engineId)
                ? (engineId, Success("Avatar Engine identity verified."))
                : (null, Failure("invalid_response", "The Avatar Engine returned an invalid identity response."));
        }

        private async Task<AvatarBridgeOperationResult> ProbeAsync(HttpClient client, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/avatarbridge/avatar/list");
            using var response = await SendBoundedAsync(client, request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? Success("Avatar Engine session is active.")
                : await ReadErrorAsync(response).ConfigureAwait(false);
        }

        private async Task<AvatarBridgeOperationResult> ActivateAsync(
            HttpClient client,
            AvatarAccessMode accessMode, CancellationToken cancellationToken = default)
        {
            using var request = CreateJsonRequest(
                HttpMethod.Post,
                "/avatarbridge/activate",
                new ActivationRequest { AccessMode = accessMode.ToString() });
            using var response = await SendBoundedAsync(client, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await ReadErrorAsync(response).ConfigureAwait(false);
            }

            var body = await DeserializeAsync<ActivationResponse>(response).ConfigureAwait(false);
            return body?.Success == true &&
                   string.Equals(body.AccessMode, accessMode.ToString(), StringComparison.Ordinal)
                ? Success(body.Description)
                : Failure("invalid_response", "The Avatar Engine returned an invalid activation response.");
        }

        private async Task<AvatarBridgeOperationResult> ReadErrorAsync(HttpResponseMessage response)
        {
            EngineErrorResponse? body = null;
            try
            {
                body = await DeserializeAsync<EngineErrorResponse>(response).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Failure(
                    "invalid_response",
                    "The Avatar Engine returned an invalid error response.",
                    GetRetryAfterSeconds(response));
            }

            return Failure(
                string.IsNullOrWhiteSpace(body?.ErrorCode) ? "request_failed" : body.ErrorCode,
                string.IsNullOrWhiteSpace(body?.Description)
                    ? $"Avatar Engine request failed with HTTP {(int)response.StatusCode}."
                    : body.Description,
                GetRetryAfterSeconds(response));
        }

        private static int? GetRetryAfterSeconds(HttpResponseMessage response)
        {
            if (response.Headers.RetryAfter?.Delta is { } delta)
            {
                return Math.Max(0, (int)Math.Ceiling(delta.TotalSeconds));
            }

            if (response.Headers.RetryAfter?.Date is { } date)
            {
                return Math.Max(0, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
            }

            return null;
        }

        private static HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string route, T body) =>
            new(method, route)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

        private static async Task<HttpResponseMessage> SendBoundedAsync(
            HttpClient client,
            HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            return await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }

        private async Task<T?> DeserializeAsync<T>(HttpResponseMessage response) =>
            await JsonSerializer.DeserializeAsync<T>(
                await response.Content.ReadAsStreamAsync().ConfigureAwait(false),
                _jsonOptions).ConfigureAwait(false);

        private static AvatarBridgeOperationResult Success(string? description) => new()
        {
            Success = true,
            Description = description ?? string.Empty
        };

        private static AvatarBridgeOperationResult Failure(
            string errorCode,
            string description,
            int? retryAfterSeconds = null) => new()
        {
            ErrorCode = errorCode,
            Description = description,
            RetryAfterSeconds = retryAfterSeconds
        };

        private static string NormalizeBaseUrl(string baseUrl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("Base URL must be an absolute HTTP or HTTPS URL.", nameof(baseUrl));
            }

            return uri.AbsoluteUri.TrimEnd('/');
        }

        public async Task<AvatarSpeechQueueFileResponse> QueueFileSpeechAsync(string fileName, string message)
        {
            var request = new AvatarSpeechQueueFileRequest
            {
                FileName = fileName,
                Message = message
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client = HttpClient;

                _logger.LogInformation("Sending speech queue-file request to: {Url}", $"{client.BaseAddress}avatarbridge/speech/queue-file");
                _logger.LogDebug("Request Body: FileName={FileName}, Message={Message}", fileName, message);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                var httpResponse = await client.PostAsync("/avatarbridge/speech/queue-file", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarSpeechQueueFileResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarSpeechQueueFileResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarSpeechQueueFileResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarSpeechQueueResponse> QueueSpeechAsync(string url, string message, string? expectedBaseUrl = null)
        {
            var request = new AvatarSpeechQueueRequest
            {
                Url = url,
                Message = message
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var client = await CreateSpeechClientAsync(expectedBaseUrl, isQueue: true).ConfigureAwait(false);

                _logger.LogInformation("Sending speech queue request to: {Url}", $"{client.BaseAddress}avatarbridge/speech/queue");
                _logger.LogDebug("Request Body: Url={Url}, Message={Message}", url, message);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                using var httpResponse = await client.PostAsync("/avatarbridge/speech/queue", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarSpeechQueueResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarSpeechQueueResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarSpeechQueueResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarSpeechStopResponse> StopSpeechAsync(bool forcedStop = false, string? baseUrl = null)
        {
            var request = new AvatarSpeechStopRequest
            {
                ForcedStop = forcedStop
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var client = await CreateSpeechClientAsync(baseUrl, isQueue: false).ConfigureAwait(false);

                _logger.LogInformation("Sending speech stop request to: {Url}", $"{client.BaseAddress}avatarbridge/speech/stop");
                _logger.LogDebug("Request Body: ForcedStop={ForcedStop}", forcedStop);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                using var httpResponse = await client.PostAsync("/avatarbridge/speech/stop", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarSpeechStopResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarSpeechStopResponse { Success = false, Description = "Empty response body", Stopped = false };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarSpeechStopResponse { Success = false, Description = ex.Message, Stopped = false };
            }
        }

        private async Task<HttpClient> CreateSpeechClientAsync(string? baseUrl, bool isQueue)
        {
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var target = NormalizeBaseUrl(baseUrl ?? HttpClient.BaseAddress!.AbsoluteUri);
                AuthenticationHeaderValue? authorization;
                if (isQueue || baseUrl is null)
                {
                    if (!string.Equals(target, NormalizeBaseUrl(HttpClient.BaseAddress!.AbsoluteUri), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("The Avatar Engine changed before speech was queued. Retry the reply.");
                    }

                    authorization = HttpClient.DefaultRequestHeaders.Authorization;
                    // A late admission may need another stop after a connection or pairing change.
                    if (isQueue) { _speechTargets[target] = authorization; }
                }
                else if (!_speechTargets.TryGetValue(target, out authorization))
                {
                    throw new InvalidOperationException("No speech was queued to the requested Avatar Engine.");
                }

                var client = _httpClientFactory.CreateClient(EndpointClientName);
                client.BaseAddress = new Uri(target);
                client.DefaultRequestHeaders.Authorization = authorization;
                return client;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        public async Task<AvatarSpeechVolumeResponse> SetSpeechVolumeAsync(float volume)
        {
            var request = new AvatarSpeechVolumeRequest
            {
                Volume = volume
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client = HttpClient;

                _logger.LogInformation("Sending speech volume request to: {Url}", $"{client.BaseAddress}avatarbridge/speech/volume");
                _logger.LogDebug("Request Body: Volume={Volume}", volume);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                var httpResponse = await client.PostAsync("/avatarbridge/speech/volume", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarSpeechVolumeResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarSpeechVolumeResponse { Success = false, Description = "Empty response body", Volume = 0.0f };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarSpeechVolumeResponse { Success = false, Description = ex.Message, Volume = 0.0f };
            }
        }

        public async Task<AvatarListResponse> ListAvatarsAsync()
        {
            try
            {
                var client = HttpClient;

                _logger.LogInformation("Sending list request to: {Url}", $"{client.BaseAddress}avatarbridge/avatar/list");

                var httpResponse = await client.GetAsync("/avatarbridge/avatar/list").ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarListResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarListResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarListResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarSelectResponse> SelectAvatarAsync(string avatarName)
        {
            var request = new AvatarSelectRequest
            {
                AvatarName = avatarName
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client = HttpClient;

                _logger.LogInformation("Sending select request to: {Url}", $"{client.BaseAddress}avatarbridge/avatar/select");
                _logger.LogDebug("Request Body: AvatarName={AvatarName}", avatarName);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                var httpResponse = await client.PostAsync("/avatarbridge/avatar/select", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarSelectResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarSelectResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarSelectResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarHairListResponse> ListHairOptionsAsync(string avatarName)
        {
            try
            {
                var client = HttpClient;
                var route = $"/avatarbridge/hair/list?AvatarName={Uri.EscapeDataString(avatarName)}";

                _logger.LogDebug("Sending hair list request to: {Url}", $"{client.BaseAddress}{route.TrimStart('/')}");

                var httpResponse = await client.GetAsync(route).ConfigureAwait(false);

                _logger.LogDebug("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarHairListResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarHairListResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarHairListResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarHairSelectResponse> SelectHairAsync(string avatarName, string hairId)
        {
            var request = new AvatarHairSelectRequest
            {
                AvatarName = avatarName,
                HairId = hairId
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var client = HttpClient;

                _logger.LogDebug("Sending hair select request to: {Url}", $"{client.BaseAddress}avatarbridge/hair/select");
                _logger.LogDebug("Request Body: AvatarName={AvatarName}, HairId={HairId}", avatarName, hairId);

                var httpResponse = await client.PostAsync("/avatarbridge/hair/select", content).ConfigureAwait(false);

                _logger.LogDebug("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarHairSelectResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarHairSelectResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarHairSelectResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarClothingListResponse> ListClothingOptionsAsync(string avatarName)
        {
            try
            {
                var client = HttpClient;
                var route = $"/avatarbridge/clothing/list?AvatarName={Uri.EscapeDataString(avatarName)}";

                _logger.LogDebug("Sending clothing list request to: {Url}", $"{client.BaseAddress}{route.TrimStart('/')}");

                var httpResponse = await client.GetAsync(route).ConfigureAwait(false);

                _logger.LogDebug("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarClothingListResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarClothingListResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarClothingListResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarClothingSelectResponse> SelectClothingAsync(string avatarName, string slot, string clothingId)
        {
            var request = new AvatarClothingSelectRequest
            {
                AvatarName = avatarName,
                Slot = slot,
                ClothingId = clothingId
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var client = HttpClient;

                _logger.LogDebug("Sending clothing select request to: {Url}", $"{client.BaseAddress}avatarbridge/clothing/select");
                _logger.LogDebug("Request Body: AvatarName={AvatarName}, Slot={Slot}, ClothingId={ClothingId}", avatarName, slot, clothingId);

                var httpResponse = await client.PostAsync("/avatarbridge/clothing/select", content).ConfigureAwait(false);

                _logger.LogDebug("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarClothingSelectResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarClothingSelectResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarClothingSelectResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarEmotionListResponse> ListEmotionsAsync()
        {
            try
            {
                var client = HttpClient;

                _logger.LogInformation("Sending list emotions request to: {Url}", $"{client.BaseAddress}avatarbridge/emotion/list");

                var httpResponse = await client.GetAsync("/avatarbridge/emotion/list").ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarEmotionListResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarEmotionListResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarEmotionListResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarEmotionResponse> SetEmotionAsync(string emotionName, float blendSpeed, float intensity = 1f)
        {
            if (!float.IsFinite(intensity) || intensity < 0f || intensity > 1f)
            {
                return new AvatarEmotionResponse { Success = false, Description = "Intensity must be a finite number between 0 and 1." };
            }

            var request = new AvatarEmotionRequest
            {
                EmotionName = emotionName,
                BlendSpeed = blendSpeed,
                // The engine's Blueprint converts normalized strength to ZenBlink percent.
                Intensity = intensity
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client = HttpClient;

                _logger.LogInformation("Sending set emotion request to: {Url}", $"{client.BaseAddress}avatarbridge/emotion/select");
                _logger.LogDebug("Request Body: EmotionName={EmotionName}, BlendSpeed={BlendSpeed}, Intensity={Intensity}", emotionName, blendSpeed, intensity);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                var httpResponse = await client.PostAsync("/avatarbridge/emotion/select", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarEmotionResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarEmotionResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarEmotionResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarBackgroundColorResponse> ChangeBackgroundColorAsync(int red, int green, int blue)
        {
            var request = new AvatarBackgroundColorRequest
            {
                Red = red,
                Green = green,
                Blue = blue
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client = HttpClient;

                _logger.LogInformation("Sending background color request to: {Url}", $"{client.BaseAddress}avatarbridge/background/color");
                _logger.LogDebug("Request Body: Red={Red}, Green={Green}, Blue={Blue}", red, green, blue);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                var httpResponse = await client.PostAsync("/avatarbridge/background/color", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarBackgroundColorResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarBackgroundColorResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarBackgroundColorResponse { Success = false, Description = ex.Message };
            }
        }

        /// <summary>Validates and applies the complete presentation through the authenticated bridge.</summary>
        public async Task<AvatarPresentationSettingsResponse> SetPresentationSettingsAsync(AvatarPresentationSettingsRequest settings)
        {
            var error = ValidatePresentationSettings(settings);
            if (error is not null)
            {
                return new AvatarPresentationSettingsResponse { Success = false, Description = error };
            }

            try
            {
                using var content = new StringContent(JsonSerializer.Serialize(settings), Encoding.UTF8, "application/json");
                var client = HttpClient;
                _logger.LogInformation("Applying presentation mode {Mode} at {Url}.", settings.Mode, client.BaseAddress);
                using var response = await client.PostAsync("/avatarbridge/presentation/settings", content).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return (await DeserializeAsync<AvatarPresentationSettingsResponse>(response).ConfigureAwait(false))!;
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarPresentationSettingsResponse { Success = false, Description = ex.Message };
            }
        }

        // Reject the whole configuration before sending any partial presentation update.
        private static string? ValidatePresentationSettings(AvatarPresentationSettingsRequest settings)
        {
            if (settings is null) { return "Presentation settings are required."; }

            if (settings.Mode is not ("Auto" or "Subtitle" or "Side")) { return "Mode must be Auto, Subtitle or Side."; }

            if (!float.IsFinite(settings.DurationSeconds) || settings.DurationSeconds < 0f)
            {
                return "DurationSeconds must be a finite number greater than or equal to zero.";
            }

            foreach (var (name, layout) in new[] { ("Subtitle", settings.Subtitle), ("Side", settings.Side) })
            {
                if (layout is null) { return $"{name} is required."; }

                if (!float.IsFinite(layout.Yaw) || layout.Yaw < -5f || layout.Yaw > 5f) { return $"{name}.Yaw must be finite and between -5 and 5."; }

                if (layout.FontSize < 8 || layout.FontSize > 120) { return $"{name}.FontSize must be an integer from 8 through 120."; }

                foreach (var (field, value) in new[] { ("PositionX", layout.PositionX), ("PositionY", layout.PositionY), ("Red", layout.Red), ("Green", layout.Green), ("Blue", layout.Blue), ("Alpha", layout.Alpha) })
                {
                    if (!float.IsFinite(value) || value < 0f || value > 1f) { return $"{name}.{field} must be finite and between 0 and 1."; }
                }

                foreach (var (field, value) in new[] { ("Width", layout.Width), ("Height", layout.Height) })
                {
                    if (!float.IsFinite(value) || value <= 0f || value > 1f) { return $"{name}.{field} must be finite, greater than 0 and at most 1."; }
                }

                if (layout.PositionX + layout.Width > 1f || layout.PositionY + layout.Height > 1f)
                {
                    return $"{name} rectangle must fit within the screen: PositionX + Width and PositionY + Height must be at most 1.";
                }
            }

            return null;
        }

        public async Task<AvatarMessageTextResponse> ShowMessageAsync(string message)
        {
            var request = new AvatarMessageTextRequest
            {
                Message = message
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client = HttpClient;

                _logger.LogInformation("Sending message text request to: {Url}", $"{client.BaseAddress}avatarbridge/message/text");
                _logger.LogDebug("Request Body: Message={Message}", message);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                var httpResponse = await client.PostAsync("/avatarbridge/message/text", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarMessageTextResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarMessageTextResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarMessageTextResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarMessageClearResponse> ClearMessageAsync()
        {
            try
            {
                using var content = new StringContent("{}", Encoding.UTF8, "application/json");

                var client = HttpClient;

                _logger.LogInformation("Sending message clear request to: {Url}", $"{client.BaseAddress}avatarbridge/message/clear");
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                var httpResponse = await client.PostAsync("/avatarbridge/message/clear", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarMessageClearResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarMessageClearResponse { Success = false, Description = "Empty response body", Cleared = false };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarMessageClearResponse { Success = false, Description = ex.Message, Cleared = false };
            }
        }

        public async Task<AvatarResolutionResponse> ChangeResolutionAsync(int width, int height, bool fullscreen)
        {
            var request = new AvatarResolutionRequest
            {
                Width = width,
                Height = height,
                Fullscreen = fullscreen
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client = HttpClient;

                _logger.LogInformation("Sending resolution request to: {Url}", $"{client.BaseAddress}avatarbridge/resolution");
                _logger.LogDebug("Request Body: Width={Width}, Height={Height}, Fullscreen={Fullscreen}", width, height, fullscreen);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                var httpResponse = await client.PostAsync("/avatarbridge/resolution", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarResolutionResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarResolutionResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarResolutionResponse { Success = false, Description = ex.Message };
            }
        }

        public async Task<AvatarGraphicsPresetResponse> SetGraphicsPresetAsync(int level, bool isRayTracingEnabled)
        {
            var request = new AvatarGraphicsPresetRequest
            {
                Level = level,
                RayTracingEnabled = isRayTracingEnabled
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var client = HttpClient;

                _logger.LogInformation("Sending graphics preset request to: {Url}", $"{client.BaseAddress}avatarbridge/graphics/preset");
                _logger.LogDebug("Request Body: Level={Level}, RayTracingEnabled={RayTracingEnabled}", level, isRayTracingEnabled);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                var httpResponse = await client.PostAsync("/avatarbridge/graphics/preset", content).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarGraphicsPresetResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarGraphicsPresetResponse { Success = false, Description = "Empty response body" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarGraphicsPresetResponse { Success = false, Description = ex.Message };
            }
        }

        public Task<AvatarHealthCheckResponse> CheckHealthAsync()
        {
            return CheckHealthAsync(HttpClient);
        }

        private async Task<AvatarHealthCheckResponse> CheckHealthAsync(HttpClient client)
        {
            try
            {
                _logger.LogInformation("Sending health check request to: {Url}", $"{client.BaseAddress}avatarbridge/health");

                // Internal timeout to prevent hanging on unresponsive services
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var httpResponse = await client.GetAsync("/avatarbridge/health", cts.Token).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarHealthCheckResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(), _jsonOptions)
.ConfigureAwait(false) ?? new AvatarHealthCheckResponse { Success = false, Status = "unknown", Version = "unknown" };
            }
            catch (Exception ex)
            {
                LogException(ex);
                return new AvatarHealthCheckResponse { Success = false, Status = "error", Version = "unknown" };
            }
        }

        public async Task<AvatarShutdownResponse> ShutdownAsync(string expectedBaseUrl, bool forceShutdown = false, CancellationToken ct = default)
        {
            var request = new AvatarShutdownRequest
            {
                ForceShutdown = forceShutdown
            };

            try
            {
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpClient client;
                await _connectLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!string.Equals(ConnectedBaseUrl, expectedBaseUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        return new AvatarShutdownResponse { Success = false, Description = "The connected Avatar Engine changed. Confirm shutdown for the current Engine." };
                    }
                    // Pin the confirmed connection so another Connect cannot redirect or cancel this action.
                    client = _httpClientFactory.CreateClient(EndpointClientName);
                    client.BaseAddress = HttpClient.BaseAddress;
                    client.DefaultRequestHeaders.Authorization = HttpClient.DefaultRequestHeaders.Authorization;
                }
                finally
                {
                    _connectLock.Release();
                }

                using var ownedClient = client;

                _logger.LogInformation("Sending shutdown request to: {Url}", $"{client.BaseAddress}avatarbridge/shutdown");
                _logger.LogDebug("Request Body: ForceShutdown={ForceShutdown}", forceShutdown);
                _logger.LogDebug("Content-Length: {Length}", content.Headers.ContentLength);

                using var httpResponse = await client.PostAsync("/avatarbridge/shutdown", content, ct).ConfigureAwait(false);

                _logger.LogInformation("Response Status Code: {StatusCode} {ReasonPhrase}", (int)httpResponse.StatusCode, httpResponse.ReasonPhrase);
                var responseBody = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogDebug("Response Body: {Body}", responseBody);

                httpResponse.EnsureSuccessStatusCode();

                return await JsonSerializer.DeserializeAsync<AvatarShutdownResponse>(
                    await httpResponse.Content.ReadAsStreamAsync(ct), _jsonOptions, ct)
.ConfigureAwait(false) ?? new AvatarShutdownResponse { Success = false, Description = "Empty response body", ShutdownInitiated = false };
            }
            // A stopped run remains cancellation instead of becoming a bridge failure.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogException(ex);
                return new AvatarShutdownResponse { Success = false, Description = ex.Message, ShutdownInitiated = false };
            }
        }

        private sealed class IdentityResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; init; }

            [JsonPropertyName("engineId")]
            public string EngineId { get; init; } = string.Empty;
        }

        private sealed class PairingStartResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; init; }

            [JsonPropertyName("description")]
            public string Description { get; init; } = string.Empty;

            [JsonPropertyName("expiresInSeconds")]
            public int ExpiresInSeconds { get; init; }
        }

        private sealed class PairingCompleteRequest
        {
            [JsonPropertyName("pairingCode")]
            public string PairingCode { get; init; } = string.Empty;

            [JsonPropertyName("clientName")]
            public string ClientName { get; init; } = string.Empty;
        }

        private sealed class PairingCompleteResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; init; }

            [JsonPropertyName("description")]
            public string Description { get; init; } = string.Empty;

            [JsonPropertyName("engineId")]
            public string EngineId { get; init; } = string.Empty;

            [JsonPropertyName("clientId")]
            public string ClientId { get; init; } = string.Empty;

            [JsonPropertyName("bridgeToken")]
            public string BridgeToken { get; init; } = string.Empty;
        }

        private sealed class ActivationRequest
        {
            [JsonPropertyName("accessMode")]
            public string AccessMode { get; init; } = string.Empty;
        }

        private sealed class ActivationResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; init; }

            [JsonPropertyName("description")]
            public string Description { get; init; } = string.Empty;

            [JsonPropertyName("accessMode")]
            public string AccessMode { get; init; } = string.Empty;
        }

        private sealed class EngineErrorResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; init; }

            [JsonPropertyName("errorCode")]
            public string ErrorCode { get; init; } = string.Empty;

            [JsonPropertyName("description")]
            public string Description { get; init; } = string.Empty;
        }

        private void LogException(Exception ex)
        {
            if (ex is HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "HTTP Request failed: {Message}", httpEx.Message);
                if (httpEx.StatusCode.HasValue)
                {
                    _logger.LogError("Status Code: {StatusCode} {StatusName}", (int)httpEx.StatusCode.Value, httpEx.StatusCode.Value);
                }
            }
            else if (ex is JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "JSON Deserialization failed: {Message}", jsonEx.Message);
                _logger.LogError("Path: {Path}, Line: {Line}, Position: {Position}", jsonEx.Path, jsonEx.LineNumber, jsonEx.BytePositionInLine);
            }
            else
            {
                _logger.LogError(ex, "Request failed: {Message}. Exception Type: {Type}", ex.Message, ex.GetType().Name);
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _httpClient = null;
            _speechTargets.Clear();
            _connectLock.Dispose();
        }
    }
}
