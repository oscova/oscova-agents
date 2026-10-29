using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Hooks;
using Oscova.Agents.Policies;
using System.Globalization;
using System.Net.Http.Json;

namespace Oscova.Agents.Infrastructure.Hooks;

internal sealed class HttpHookExecutor(
    IHttpClientFactory httpClientFactory,
    ISystemCredentialService systemCredentialService,
    RunContextAccessor runContextAccessor)
{
    /// <summary>
    /// Sends a POST hook request with the standard JSON envelope and scalar headers.
    /// </summary>
    public async Task<HookDispatchResult> ExecuteAsync(
        HookDefinition definition,
        HookEnvelope envelope,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        bool recordFailedEgress = false;

        try
        {
            var http = definition.Http ?? throw new InvalidOperationException("HTTP hook options are required.");
            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, http.Url)
            {
                Content = JsonContent.Create(envelope, options: HookJsonSerializer.Options)
            };

            AddEnvelopeHeaders(request, envelope);
            AddStaticHeaders(request, http.Headers);
            var secretFailure = await AddSecretHeadersAsync(request, http.SecretHeaders, timeoutCts.Token).ConfigureAwait(false);
            if (secretFailure is not null) return secretFailure;

            timeoutCts.Token.ThrowIfCancellationRequested();
            recordFailedEgress = request.RequestUri is { } destination && !EgressClassifier.IsLocalHost(destination) && runContextAccessor.Current?.AirGapped != true;
            using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            var result = HookResponseParser.FromHttp(response, body);
            if (request.RequestUri is { } uri && !EgressClassifier.IsLocalHost(uri))
            {
                // A deliberate deny completed successfully; only transport failures count as failed dispatches.
                var ledger = runContextAccessor.Current?.ToolInvocationLedger;
                if (result.IsTransportFailure)
                {
                    ledger?.RecordFailed($"hook:{definition.Id}", NetworkScope.Egress);
                }
                else
                {
                    ledger?.RecordCompleted($"hook:{definition.Id}", NetworkScope.Egress);
                }
            }

            recordFailedEgress = false;
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return HookDispatchResult.TransportFailure("Hook HTTP request timed out.", "timeout");
        }
        finally
        {
            // Failed sends remain auditable, while offline rejections never leave the host.
            if (recordFailedEgress)
            {
                runContextAccessor.Current?.ToolInvocationLedger?.RecordFailed($"hook:{definition.Id}", NetworkScope.Egress);
            }
        }
    }

    private static void AddEnvelopeHeaders(HttpRequestMessage request, HookEnvelope envelope)
    {
        request.Headers.TryAddWithoutValidation("X-Oscova-Hook-Event-Name", envelope.HookEventName.ToString());
        request.Headers.TryAddWithoutValidation("X-Oscova-Hook-Run-Id", envelope.HookRunId);
        request.Headers.TryAddWithoutValidation("X-Oscova-Hook-Schema-Version", envelope.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Oscova-Hook-Timestamp", envelope.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        AddHeaderIfPresent(request, "X-Oscova-Hook-User-Id", envelope.UserId);
        AddHeaderIfPresent(request, "X-Oscova-Hook-Agent-Id", envelope.AgentId);
        AddHeaderIfPresent(request, "X-Oscova-Hook-Chat-Thread-Id", envelope.ChatThreadId);
        AddHeaderIfPresent(request, "X-Oscova-Hook-Task-Id", envelope.TaskId);
        AddHeaderIfPresent(request, "X-Oscova-Hook-Task-Run-Id", envelope.TaskRunId);
        AddHeaderIfPresent(request, "X-Oscova-Hook-Execution-Kind", envelope.ExecutionKind);
    }

    private static void AddHeaderIfPresent(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static void AddStaticHeaders(HttpRequestMessage request, IEnumerable<HookHeader> headers)
    {
        foreach (var header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }
    }

    private async Task<HookDispatchResult?> AddSecretHeadersAsync(
        HttpRequestMessage request,
        IEnumerable<HookSecretHeader> headers,
        CancellationToken ct)
    {
        foreach (var header in headers)
        {
            var value = string.IsNullOrWhiteSpace(header.FieldName)
                ? await systemCredentialService.ResolveFieldAsync(header.CredentialKey, ct).ConfigureAwait(false)
                : await systemCredentialService.ResolveFieldAsync(header.CredentialKey, header.FieldName, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value))
            {
                return HookDispatchResult.TransportFailure(
                    $"Hook secret credential '{header.CredentialKey}' could not be resolved.",
                    "missing_secret");
            }

            request.Headers.TryAddWithoutValidation(header.Name, value);
        }

        return null;
    }
}
