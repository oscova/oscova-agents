using Microsoft.Extensions.Logging;
using Oscova.Agents.Avatar.Contracts;
using Oscova.Agents.Core.ApiContracts.Avatar;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Avatar;

public partial class AvatarBridgeService
{
    /// <summary>Reads the Engine identity without requiring pairing.</summary>
    public Task<AvatarBridgeEndpointResult> ReadIdentityAsync(string baseUrl, CancellationToken cancellationToken) =>
        SendEndpointAsync(baseUrl, AvatarAccessMode.Shared, HttpMethod.Get, "/avatarbridge/identity", null, false, cancellationToken);

    /// <summary>Pairs the installation and returns only nonsecret completion fields.</summary>
    public async Task<AvatarBridgeEndpointResult> PairEndpointAsync(
        string baseUrl, string pairingCode, string clientName, CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeBaseUrl(baseUrl);
        pairingCode = pairingCode?.Trim() ?? string.Empty;
        if (pairingCode.Length != 6 || pairingCode.Any(character => character is < '0' or > '9'))
            return FromOperationResult(Failure("pairing_code_malformed", "The pairing code must contain exactly six digits."));
        if (string.IsNullOrWhiteSpace(clientName) || clientName.Length > 100 || clientName.Any(char.IsControl))
            return FromOperationResult(Failure("client_name_invalid", "Client name must be 1 to 100 characters without control characters."));

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _httpClientFactory.CreateClient(EndpointClientName);
            client.BaseAddress = new Uri(normalizedUrl);
            var identity = await GetIdentityAsync(client, cancellationToken).ConfigureAwait(false);
            if (!identity.Result.Success || identity.EngineId is null) return FromOperationResult(identity.Result);

            using var request = CreateJsonRequest(HttpMethod.Post, "/avatarbridge/pairing/complete",
                new PairingCompleteRequest { PairingCode = pairingCode, ClientName = clientName });
            using var response = await SendBoundedAsync(client, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return FromOperationResult(await ReadErrorAsync(response).ConfigureAwait(false));

            // Consume the secret response here. Only allowlisted fields leave this owner.
            var body = await DeserializeAsync<PairingCompleteResponse>(response).ConfigureAwait(false);
            if (body?.Success != true || !Guid.TryParse(body.EngineId, out var engineId) ||
                engineId != identity.EngineId || !Guid.TryParse(body.ClientId, out var clientId) ||
                clientId == Guid.Empty || string.IsNullOrWhiteSpace(body.BridgeToken))
                return FromOperationResult(Failure("invalid_response", "The Avatar Engine returned an invalid pairing response."));

            try
            {
                await _credentialStore.SaveAsync(normalizedUrl, engineId, clientId, body.BridgeToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist the Avatar Bridge credential.");
                return FromOperationResult(Failure("credential_persistence_failed",
                    "The pairing credential could not be saved. Fix app_data permissions and pair again with a new code."));
            }

            _credentialStale = true;
            return new(false, JsonSerializer.SerializeToElement(new
            {
                success = true, description = body.Description, engineId, clientId
            }));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Avatar Engine returned an invalid pairing response.");
            return FromOperationResult(Failure("invalid_response", "The Avatar Engine returned an invalid pairing response."));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Avatar pairing could not reach the Engine.");
            return FromOperationResult(Failure("unreachable", "The Avatar Engine could not be reached."));
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Sends a fixed control endpoint using the installation credential.</summary>
    public async Task<AvatarBridgeEndpointResult> SendEndpointAsync(
        string baseUrl, AvatarAccessMode accessMode, HttpMethod method, string relativePath,
        object? body, bool requireActivation, CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeBaseUrl(baseUrl);
        ArgumentOutOfRangeException.ThrowIfNotEqual(Enum.IsDefined(accessMode), true, nameof(accessMode));
        if (!relativePath.StartsWith("/avatarbridge/", StringComparison.Ordinal) ||
            relativePath.Contains("..", StringComparison.Ordinal) || relativePath.Contains('\\') ||
            relativePath.StartsWith("/avatarbridge/pairing/complete", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only fixed Avatar control routes are allowed.", nameof(relativePath));

        var isPublic = relativePath is "/avatarbridge/identity" or "/avatarbridge/pairing/start";
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                HttpClient client;
                await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!isPublic)
                    {
                        var preparation = await PrepareConnectionAsync(normalizedUrl, accessMode, requireActivation, cancellationToken).ConfigureAwait(false);
                        if (!preparation.Success) return FromOperationResult(preparation);
                    }

                    client = _httpClientFactory.CreateClient(EndpointClientName);
                    client.BaseAddress = new Uri(normalizedUrl);
                    if (!isPublic) client.DefaultRequestHeaders.Authorization = _httpClient!.DefaultRequestHeaders.Authorization;
                }
                finally
                {
                    _connectLock.Release();
                }

                // Each action owns its client so setup changes cannot cancel it or block speech-stop.
                using (client)
                using (var request = body is null ? new HttpRequestMessage(method, relativePath) : CreateJsonRequest(method, relativePath, body))
                using (var response = isPublic
                    ? await SendBoundedAsync(client, request, cancellationToken).ConfigureAwait(false)
                    : await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    var result = await ReadEndpointResultAsync(response, cancellationToken).ConfigureAwait(false);
                    var errorCode = GetString(result.Payload, "errorCode");
                    if (!isPublic && result.IsError && attempt == 0 &&
                        (errorCode == "pairing_required" || (requireActivation && errorCode == "activation_required")))
                    {
                        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            if (errorCode == "pairing_required") _credentialStale = true;
                            else _activeAccessMode = null;
                        }
                        finally
                        {
                            _connectLock.Release();
                        }

                        continue;
                    }

                    if (!result.IsError && relativePath == "/avatarbridge/activate")
                    {
                        if (!string.Equals(GetString(result.Payload, "accessMode"), accessMode.ToString(), StringComparison.Ordinal))
                            return FromOperationResult(Failure("invalid_response", "The Engine returned an unexpected access mode."));
                        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            if (string.Equals(ConnectedBaseUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase) &&
                                Equals(_httpClient?.DefaultRequestHeaders.Authorization, client.DefaultRequestHeaders.Authorization))
                            {
                                _activeAccessMode = accessMode;
                                Interlocked.Increment(ref _sessionVersion);
                            }
                        }
                        finally
                        {
                            _connectLock.Release();
                        }
                    }

                    return result;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Avatar endpoint {Route} returned invalid JSON.", relativePath.Split('?')[0]);
            return FromOperationResult(Failure("invalid_response", "The Avatar Engine returned an invalid response."));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Avatar endpoint {Route} could not complete.", relativePath.Split('?')[0]);
            return FromOperationResult(Failure("unreachable", "The Avatar request did not complete. Its outcome may be unknown. Check the Engine before retrying."));
        }

        return FromOperationResult(Failure("request_failed", "The Avatar request failed."));
    }

    private static async Task<AvatarBridgeEndpointResult> ReadEndpointResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = document.RootElement;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("success", out var success) ||
            success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return FromOperationResult(Failure("invalid_response", "The Avatar Engine returned an invalid response."));

        var isError = !response.IsSuccessStatusCode || !success.GetBoolean();
        if (isError && GetRetryAfterSeconds(response) is { } retryAfterSeconds)
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload.GetRawText())!;
            fields["retryAfterSeconds"] = JsonSerializer.SerializeToElement(retryAfterSeconds);
            return new(true, JsonSerializer.SerializeToElement(fields));
        }

        return new(isError, payload.Clone());
    }

    private static AvatarBridgeEndpointResult FromOperationResult(AvatarBridgeOperationResult result) =>
        new(!result.Success, JsonSerializer.SerializeToElement(new
        {
            success = result.Success, description = result.Description,
            errorCode = result.ErrorCode, retryAfterSeconds = result.RetryAfterSeconds
        }));

    private static AvatarBridgeOperationResult ToOperationResult(AvatarBridgeEndpointResult result) => new()
    {
        Success = !result.IsError,
        Description = GetString(result.Payload, "description") ?? string.Empty,
        ErrorCode = GetString(result.Payload, "errorCode"),
        RetryAfterSeconds = result.Payload.TryGetProperty("retryAfterSeconds", out var retry) &&
            retry.ValueKind == JsonValueKind.Number && retry.TryGetInt32(out var seconds) ? seconds : null
    };

    private static string? GetString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
